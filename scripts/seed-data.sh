#!/usr/bin/env bash
#
# Seeds default plans and client assignments into the AI Policy API.
# Can be run after Terraform apply or standalone.
#
# Creates Enterprise and Starter plans, then assigns sample clients to them.
# Uses the Container App's managed identity via the APIM gateway, or directly
# if run with a valid bearer token. Idempotent — updates existing plans/clients.
#
# Usage:
#   ./seed-data.sh [--base-url URL] [--client1-app-id ID] [--client2-app-id ID]
#                  [--tenant-id ID] [--secondary-tenant-id ID] [--api-app-id ID]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TF_DIR="$SCRIPT_DIR/../infra/terraform"

BASE_URL=""
CLIENT1_APP_ID=""
CLIENT2_APP_ID=""
TENANT_ID=""
SECONDARY_TENANT_ID=""
API_APP_ID=""

# Parse named arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url)         BASE_URL="$2"; shift 2 ;;
        --client1-app-id)   CLIENT1_APP_ID="$2"; shift 2 ;;
        --client2-app-id)   CLIENT2_APP_ID="$2"; shift 2 ;;
        --tenant-id)        TENANT_ID="$2"; shift 2 ;;
        --secondary-tenant-id) SECONDARY_TENANT_ID="$2"; shift 2 ;;
        --api-app-id)       API_APP_ID="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

# Read from Terraform outputs if not provided
if [[ -z "$BASE_URL" || -z "$CLIENT1_APP_ID" || -z "$CLIENT2_APP_ID" || -z "$TENANT_ID" || -z "$API_APP_ID" ]]; then
    echo -e "\033[90mReading Terraform outputs...\033[0m"
    tf_output="$(cd "$TF_DIR" && terraform output -json)"

    [[ -z "$BASE_URL" ]]       && BASE_URL="$(echo "$tf_output" | jq -r '.container_app_url.value')"
    [[ -z "$API_APP_ID" ]]     && API_APP_ID="$(echo "$tf_output" | jq -r '.api_app_id.value')"
    [[ -z "$CLIENT1_APP_ID" ]] && CLIENT1_APP_ID="$(echo "$tf_output" | jq -r '.client1_app_id.value')"
    [[ -z "$CLIENT2_APP_ID" ]] && CLIENT2_APP_ID="$(echo "$tf_output" | jq -r '.client2_app_id.value')"
    [[ -z "$TENANT_ID" ]]      && TENANT_ID="$(echo "$tf_output" | jq -r '.tenant_id.value')"
    [[ -z "$SECONDARY_TENANT_ID" ]] && SECONDARY_TENANT_ID="$(echo "$tf_output" | jq -r '.secondary_tenant_id.value // empty')"
fi

BASE_URL="${BASE_URL%/}"
echo -e "\033[36mSeeding data to: $BASE_URL\033[0m"

# Acquire a token using the Sample Client's credentials (it has the Admin role).
echo -e "\033[90mAcquiring access token via client credentials...\033[0m"
TOKEN=""

CLIENT1_SECRET="$(cd "$TF_DIR" && terraform output -raw client1_secret 2>/dev/null || true)"

if [[ -n "$CLIENT1_APP_ID" && -n "$CLIENT1_SECRET" && -n "$TENANT_ID" ]]; then
    token_response="$(curl -s -X POST \
        "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
        -d "grant_type=client_credentials&client_id=$CLIENT1_APP_ID&client_secret=$CLIENT1_SECRET&scope=api://$API_APP_ID/.default" \
        -H "Content-Type: application/x-www-form-urlencoded" || true)"

    TOKEN="$(echo "$token_response" | jq -r '.access_token // empty')"
    if [[ -z "$TOKEN" ]]; then
        error_desc="$(echo "$token_response" | jq -r '.error_description // empty')"
        echo -e "\033[33m  Client credentials flow failed: ${error_desc:-unknown error}\033[0m"
    fi
fi

# Fallback: try az cli token
if [[ -z "$TOKEN" ]]; then
    echo -e "\033[33m  Falling back to az cli token...\033[0m"
    TOKEN="$(az account get-access-token --resource "api://$API_APP_ID" --query "accessToken" -o tsv 2>/dev/null || true)"
fi

if [[ -z "$TOKEN" ]]; then
    echo "Could not acquire a token. Ensure Terraform has been applied and you are logged into the correct tenant with 'az login --tenant $TENANT_ID'." >&2
    exit 1
fi
echo -e "\033[32m  ✓ Token acquired\033[0m"

# Wait for Container App to be ready (health check is unauthenticated)
echo -e "\033[90mWaiting for Container App to be ready...\033[0m"
MAX_RETRIES=12
RETRY_COUNT=0
READY=false

while [[ "$READY" == "false" && $RETRY_COUNT -lt $MAX_RETRIES ]]; do
    if curl -sf --max-time 10 "$BASE_URL/health" >/dev/null 2>&1; then
        READY=true
    elif curl -sf --max-time 10 "$BASE_URL/api/plans" >/dev/null 2>&1; then
        READY=true
    else
        RETRY_COUNT=$((RETRY_COUNT + 1))
        echo -e "\033[90m  Attempt $RETRY_COUNT/$MAX_RETRIES — waiting 10s...\033[0m"
        sleep 10
    fi
done

if [[ "$READY" == "false" ]]; then
    echo "Container App not responding after $MAX_RETRIES attempts." >&2
    exit 1
fi
echo -e "\033[32m  ✓ Container App is ready\033[0m"

# Helper: call API
api() {
    local method="$1" url="$2" body="${3:-}"
    if [[ -n "$body" ]]; then
        curl -sf --max-time 15 -X "$method" "$url" \
            -H "Authorization: Bearer $TOKEN" \
            -H "Content-Type: application/json" \
            -d "$body"
    else
        curl -sf --max-time 15 -X "$method" "$url" \
            -H "Authorization: Bearer $TOKEN" \
            -H "Content-Type: application/json"
    fi
}

# Fetch existing plans
plans_response="$(api GET "$BASE_URL/api/plans")"

# Ensure Enterprise plan
echo -e "\033[90mEnsuring Enterprise plan...\033[0m"
ENT_PLAN_BODY='{
    "name": "Enterprise",
    "monthlyRate": 999.99,
    "monthlyTokenQuota": 10000000,
    "tokensPerMinuteLimit": 200000,
    "requestsPerMinuteLimit": 120,
    "allowOverbilling": true,
    "costPerMillionTokens": 10.0
}'

enterprise_plan_id="$(echo "$plans_response" | jq -r '[.plans[] | select(.name | ascii_downcase | ltrimstr(" ") | rtrimstr(" ") == "enterprise")] | first | .id // empty')"

if [[ -n "$enterprise_plan_id" ]]; then
    ent_plan="$(api PUT "$BASE_URL/api/plans/$enterprise_plan_id" "$ENT_PLAN_BODY")"
    ent_plan_id="$(echo "$ent_plan" | jq -r '.id')"
    echo -e "\033[32m  ✓ Enterprise plan updated (ID: $ent_plan_id)\033[0m"
else
    ent_plan="$(api POST "$BASE_URL/api/plans" "$ENT_PLAN_BODY")"
    ent_plan_id="$(echo "$ent_plan" | jq -r '.id')"
    echo -e "\033[32m  ✓ Enterprise plan created (ID: $ent_plan_id)\033[0m"
fi

# Ensure Starter plan
echo -e "\033[90mEnsuring Starter plan...\033[0m"
START_PLAN_BODY='{
    "name": "Starter",
    "monthlyRate": 49.99,
    "monthlyTokenQuota": 500,
    "tokensPerMinuteLimit": 1000,
    "requestsPerMinuteLimit": 10,
    "allowOverbilling": false,
    "costPerMillionTokens": 0
}'

starter_plan_id="$(echo "$plans_response" | jq -r '[.plans[] | select(.name | ascii_downcase | ltrimstr(" ") | rtrimstr(" ") == "starter")] | first | .id // empty')"

if [[ -n "$starter_plan_id" ]]; then
    start_plan="$(api PUT "$BASE_URL/api/plans/$starter_plan_id" "$START_PLAN_BODY")"
    start_plan_id="$(echo "$start_plan" | jq -r '.id')"
    echo -e "\033[32m  ✓ Starter plan updated (ID: $start_plan_id)\033[0m"
else
    start_plan="$(api POST "$BASE_URL/api/plans" "$START_PLAN_BODY")"
    start_plan_id="$(echo "$start_plan" | jq -r '.id')"
    echo -e "\033[32m  ✓ Starter plan created (ID: $start_plan_id)\033[0m"
fi

# Assign clients to plans
echo -e "\033[90mAssigning clients to plans...\033[0m"

api PUT "$BASE_URL/api/clients/$CLIENT1_APP_ID/$TENANT_ID" \
    "{\"planId\": \"$ent_plan_id\", \"displayName\": \"AI Policy Sample Client\"}" >/dev/null
echo -e "\033[32m  ✓ Client 1 → Enterprise plan (tenant: $TENANT_ID)\033[0m"

api PUT "$BASE_URL/api/clients/$CLIENT2_APP_ID/$TENANT_ID" \
    "{\"planId\": \"$start_plan_id\", \"displayName\": \"AI Policy Demo Client 2\"}" >/dev/null
echo -e "\033[32m  ✓ Client 2 → Starter plan (tenant: $TENANT_ID)\033[0m"

# Optional secondary tenant
if [[ -n "${SECONDARY_TENANT_ID:-}" ]]; then
    api PUT "$BASE_URL/api/clients/$CLIENT2_APP_ID/$SECONDARY_TENANT_ID" \
        "{\"planId\": \"$start_plan_id\", \"displayName\": \"AI Policy Demo Client 2 (Secondary Tenant)\"}" >/dev/null
    echo -e "\033[32m  ✓ Client 2 → Starter plan (secondary tenant: $SECONDARY_TENANT_ID)\033[0m"
fi

echo ""
echo -e "\033[32mSeed data complete ✓\033[0m"
echo "  Enterprise plan ID: $ent_plan_id"
echo "  Starter plan ID:    $start_plan_id"
