#!/usr/bin/env bash
set -euo pipefail

# Query deployed client assignments to diagnose duplicates

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/../demo/.env.local"

if [[ ! -f "$ENV_FILE" ]]; then
    echo "ERROR: demo/.env.local not found" >&2
    exit 1
fi

# Read variables from .env.local (skip comments and blank lines)
declare -A vars
while IFS='=' read -r key value; do
    [[ -z "$key" || "$key" == \#* ]] && continue
    vars["$key"]="$value"
done < "$ENV_FILE"

TENANT_ID="${vars[DemoClient__TenantId]}"
API_SCOPE="${vars[DemoClient__ApiScope]}"
CLIENT_ID="${vars[DemoClient__Clients__0__AppId]}"
CLIENT_SECRET="${vars[DemoClient__Clients__0__Secret]}"
BASE_URL="${vars[DemoClient__AIPolicyBase]}"

echo "Acquiring token..."
TOKEN_RESPONSE=$(curl -s -X POST \
    "https://login.microsoftonline.com/${TENANT_ID}/oauth2/v2.0/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "grant_type=client_credentials&client_id=${CLIENT_ID}&client_secret=${CLIENT_SECRET}&scope=${API_SCOPE}")

TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.access_token')

if [[ -z "$TOKEN" || "$TOKEN" == "null" ]]; then
    echo "ERROR: Failed to acquire token" >&2
    echo "$TOKEN_RESPONSE" | jq . >&2
    exit 1
fi

echo ""
echo "=== Client Assignments ==="
CLIENTS_RESPONSE=$(curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/clients")
echo "$CLIENTS_RESPONSE" | jq -r '
    ["ClientAppId", "TenantId", "DisplayName", "Usage", "PlanId"],
    ["----------", "--------", "-----------", "-----", "------"],
    (.clients[] | [.clientAppId, .tenantId, .displayName, (.currentPeriodUsage | tostring), .planId])
    | @tsv' | column -t

echo ""
echo "=== Usage Summary (Cosmos) ==="
USAGE_RESPONSE=$(curl -s -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/usage")
echo "$USAGE_RESPONSE" | jq -r '
    ["ClientAppId", "TenantId", "DeploymentId", "TotalTokens", "CostToUs"],
    ["----------", "--------", "------------", "-----------", "--------"],
    (.usageSummaries[] | [.clientAppId, .tenantId, .deploymentId, (.totalTokens | tostring), (.costToUs | tostring)])
    | @tsv' | column -t
