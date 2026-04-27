#!/usr/bin/env bash
#
# setup-azure.sh — Deploys the Azure OpenAI AI Policy Environment from scratch.
#
# Automates: Resource Group, ACR, Entra App Registrations, ACR image build,
# Bicep infrastructure, APIM configuration, and initial plan setup.
#
# Usage:
#   ./setup-azure.sh [OPTIONS]
#
# Options:
#   -l, --location LOCATION            Azure region (default: eastus2)
#   -w, --workload-name NAME           Short prefix for resources (default: aipolicy)
#   -g, --resource-group NAME          Resource group name (default: rg-{workload}-{location})
#   -s, --secondary-tenant-id ID       Optional second Entra tenant for cross-tenant demo
#       --skip-bicep                   Skip the Bicep deployment
#       --skip-build                   Skip ACR image build
#       --no-jwt                       Disable JWT-authenticated OpenAI API endpoint
#       --no-keys                      Disable subscription-key-authenticated OpenAI API endpoint
#       --no-external-demo-client      Skip the optional 'AIPolicy Demo Client 2' external demo app
#   -h, --help                         Show this help

set -uo pipefail

# ── Colors ──────────────────────────────────────────────────────────────────
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly CYAN='\033[0;36m'
readonly GRAY='\033[0;90m'
readonly MAGENTA='\033[0;35m'
readonly DARKYELLOW='\033[0;33m'
readonly NC='\033[0m'

info()    { echo -e "  ${GRAY}$*${NC}"; }
success() { echo -e "  ${GREEN}  ✓ $*${NC}"; }
warn()    { echo -e "  ${DARKYELLOW}  ⚠ $*${NC}"; }
die()     { echo -e "  ${RED}✗ $*${NC}" >&2; exit 1; }

phase_header() {
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${YELLOW}  $1${NC}"
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
}

trim_trailing_dashes() { echo "$1" | sed 's/-*$//'; }

# ── Defaults / argument parsing ─────────────────────────────────────────────
LOCATION="eastus2"
WORKLOAD_NAME="aipolicy"
RESOURCE_GROUP_NAME=""
SECONDARY_TENANT_ID=""
SKIP_BICEP=false
SKIP_BUILD=false
ENABLE_JWT=true
ENABLE_KEYS=true
INCLUDE_EXTERNAL_DEMO_CLIENT=true

usage() {
    sed -n '/^# Usage:/,/^$/p' "$0" | sed 's/^# //' | sed 's/^#//'
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -l|--location)           LOCATION="$2";              shift 2 ;;
        -w|--workload-name)      WORKLOAD_NAME="$2";         shift 2 ;;
        -g|--resource-group)     RESOURCE_GROUP_NAME="$2";   shift 2 ;;
        -s|--secondary-tenant-id) SECONDARY_TENANT_ID="$2";  shift 2 ;;
        --skip-bicep)            SKIP_BICEP=true;             shift   ;;
        --skip-build)            SKIP_BUILD=true;             shift   ;;
        --no-jwt)                ENABLE_JWT=false;            shift   ;;
        --no-keys)               ENABLE_KEYS=false;           shift   ;;
        --no-external-demo-client) INCLUDE_EXTERNAL_DEMO_CLIENT=false; shift ;;
        -h|--help)               usage ;;
        *) die "Unknown option: $1" ;;
    esac
done

[[ -z "$RESOURCE_GROUP_NAME" ]] && RESOURCE_GROUP_NAME="rg-${WORKLOAD_NAME}-${LOCATION}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# ── Resource naming ─────────────────────────────────────────────────────────
workload_token=$(echo "$WORKLOAD_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')
[[ -z "$workload_token" ]] && die "WorkloadName must contain at least one alphanumeric character."

APIM_NAME="apim-${WORKLOAD_NAME}"
CONTAINER_APP_NAME="ca-${WORKLOAD_NAME}"
CONTAINER_APP_ENV_NAME="cae-${WORKLOAD_NAME}"
REDIS_CACHE_NAME="redis-${WORKLOAD_NAME}"
COSMOS_ACCOUNT_NAME="cosmos-${WORKLOAD_NAME}"
KEY_VAULT_NAME="kv-${workload_token}"
LOG_ANALYTICS_WORKSPACE_NAME="law-${workload_token}"
APP_INSIGHTS_NAME="ai-${workload_token}"
ai_name_base="aisrv-${workload_token}"
[[ ${#ai_name_base} -gt 58 ]] && ai_name_base=$(trim_trailing_dashes "${ai_name_base:0:58}")
storage_prefix="st${workload_token}"
[[ ${#storage_prefix} -gt 19 ]] && storage_prefix="${storage_prefix:0:19}"

ACR_NAME=""
AI_SERVICE_NAME=""
STORAGE_ACCOUNT_NAME=""

[[ ${#APIM_NAME} -gt 50 ]]             && APIM_NAME=$(trim_trailing_dashes "${APIM_NAME:0:50}")
[[ ${#CONTAINER_APP_NAME} -gt 32 ]]    && CONTAINER_APP_NAME=$(trim_trailing_dashes "${CONTAINER_APP_NAME:0:32}")
[[ ${#CONTAINER_APP_ENV_NAME} -gt 32 ]] && CONTAINER_APP_ENV_NAME=$(trim_trailing_dashes "${CONTAINER_APP_ENV_NAME:0:32}")
[[ ${#REDIS_CACHE_NAME} -gt 63 ]]      && REDIS_CACHE_NAME=$(trim_trailing_dashes "${REDIS_CACHE_NAME:0:63}")
[[ ${#COSMOS_ACCOUNT_NAME} -gt 44 ]]   && COSMOS_ACCOUNT_NAME=$(trim_trailing_dashes "${COSMOS_ACCOUNT_NAME:0:44}")
[[ ${#KEY_VAULT_NAME} -gt 24 ]]        && KEY_VAULT_NAME=$(trim_trailing_dashes "${KEY_VAULT_NAME:0:24}")

echo -e "${CYAN}╔══════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║   Azure OpenAI AI Policy - Full Environment Setup        ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════════════╝${NC}"
echo ""
echo "  Location:       $LOCATION"
echo "  Workload:       $WORKLOAD_NAME"
echo "  Resource Group: $RESOURCE_GROUP_NAME"
echo ""

# ── Helper functions ────────────────────────────────────────────────────────

# az_retry — wrap az invocations with exponential-backoff retry on transient Graph/ARM errors.
# Usage: az_retry ad app update --id "$x" ...     (drop the 'az' — it is implicit)
# Prints stdout to caller's stdout, returns the final exit code. Stderr is captured for
# pattern matching and only surfaced on permanent failure (matching the existing 2>/dev/null style).
az_retry() {
    local max_attempts=5
    local attempt=0 delay exit_code
    local tmp_stdout tmp_stderr
    tmp_stdout=$(mktemp)
    tmp_stderr=$(mktemp)
    while :; do
        attempt=$((attempt + 1))
        az "$@" >"$tmp_stdout" 2>"$tmp_stderr"
        exit_code=$?
        if (( exit_code == 0 )); then
            cat "$tmp_stdout"
            rm -f "$tmp_stdout" "$tmp_stderr"
            return 0
        fi
        if (( attempt >= max_attempts )) || ! grep -qiE 'RemoteDisconnected|Connection aborted|Read timed out|ReadTimeoutError|ServiceUnavailable|BadGateway|GatewayTimeout|Too Many Requests|HTTPSConnectionPool|ConnectionReset|ConnectionError|Temporary failure in name resolution|Max retries exceeded|\(50[0234]\)|\(429\)' "$tmp_stderr"; then
            cat "$tmp_stdout"
            cat "$tmp_stderr" >&2
            rm -f "$tmp_stdout" "$tmp_stderr"
            return "$exit_code"
        fi
        delay=$(( 2 ** attempt ))
        (( delay > 30 )) && delay=30
        echo -e "  ${DARKYELLOW}  ⚠ Transient Azure CLI error (attempt ${attempt}/${max_attempts}). Retrying in ${delay}s...${NC}" >&2
        sleep "$delay"
    done
}

ensure_service_principal() {
    local app_id="$1" display_name="$2"

    local sp_id
    sp_id=$(az_retry ad sp show --id "$app_id" --query "id" -o tsv 2>/dev/null) || true
    if [[ -n "$sp_id" ]]; then
        success "Service principal exists for $display_name"
        return 0
    fi

    info "  Creating service principal for $display_name..."
    az_retry ad sp create --id "$app_id" -o none || die "Failed to create service principal for $display_name ($app_id)."

    local sp_ready=false
    for attempt in $(seq 1 10); do
        sleep 3
        sp_id=$(az_retry ad sp show --id "$app_id" --query "id" -o tsv 2>/dev/null) || true
        if [[ -n "$sp_id" ]]; then
            sp_ready=true
            break
        fi
    done
    [[ "$sp_ready" == "true" ]] || die "Service principal for $display_name ($app_id) was not discoverable after creation."
    success "Service principal ready for $display_name"
}

ensure_delegated_scope_and_consent() {
    local client_app_id="$1" api_app_id="$2" scope_id="$3" client_display_name="$4"

    local existing_access
    existing_access=$(az_retry ad app show --id "$client_app_id" \
        --query "requiredResourceAccess[?resourceAppId=='$api_app_id'].resourceAccess[].id" -o tsv 2>/dev/null) || true

    if ! echo "$existing_access" | grep -qx "$scope_id"; then
        info "  Adding delegated scope permission for $client_display_name..."
        az_retry ad app permission add --id "$client_app_id" --api "$api_app_id" \
            --api-permissions "${scope_id}=Scope" -o none \
            || die "Failed to add delegated API permission for $client_display_name."
        success "Delegated scope permission added"
    else
        success "Delegated scope permission already present for $client_display_name"
    fi

    local consent_granted=false
    for attempt in $(seq 1 10); do
        if az_retry ad app permission admin-consent --id "$client_app_id" -o none 2>/dev/null; then
            consent_granted=true
            break
        fi
        echo -e "  ${DARKYELLOW}  Admin consent attempt $attempt/10 failed for $client_display_name — retrying in 5s...${NC}"
        sleep 5
    done
    [[ "$consent_granted" == "true" ]] || die "Failed to grant admin consent for $client_display_name after retries."
    success "Admin consent granted for $client_display_name"
}

# ============================================================================
# Phase 1: Prerequisites Check
# ============================================================================
phase_header "Phase 1: Prerequisites Check"

info "Checking Azure CLI..."
az_version=$(az version -o json 2>&1) || die "Azure CLI is not installed or not in PATH."
az_cli_ver=$(echo "$az_version" | jq -r '."azure-cli"')
success "Azure CLI $az_cli_ver found"

account_json=$(az account show -o json 2>&1) || die "Not logged in to Azure CLI. Run 'az login' first."
subscription_id=$(echo "$account_json" | jq -r '.id')
tenant_id=$(echo "$account_json" | jq -r '.tenantId')
user_name=$(echo "$account_json" | jq -r '.user.name')
account_name=$(echo "$account_json" | jq -r '.name')
success "Logged in: $user_name"
success "Subscription: $account_name ($subscription_id)"
success "Tenant: $tenant_id"

info "Checking jq..."
command -v jq >/dev/null 2>&1 || die "jq is not installed. Install it with: sudo apt-get install jq"
success "jq found"

info "Checking curl..."
command -v curl >/dev/null 2>&1 || die "curl is not installed."
success "curl found"

info "Registering required Azure resource providers..."
required_providers=(
    "Microsoft.AlertsManagement"
    "Microsoft.ApiManagement"
    "Microsoft.App"
    "Microsoft.Cache"
    "Microsoft.CognitiveServices"
    "Microsoft.DocumentDB"
    "Microsoft.Insights"
    "Microsoft.KeyVault"
    "Microsoft.OperationalInsights"
    "Microsoft.Storage"
)
for ns in "${required_providers[@]}"; do
    reg_state=$(az provider show --namespace "$ns" --query "registrationState" -o tsv 2>/dev/null) || true
    if [[ "$reg_state" != "Registered" ]]; then
        info "  Registering $ns..."
        az provider register --namespace "$ns" --wait -o none || die "Failed to register provider '$ns'."
        success "$ns registered"
    else
        success "$ns already registered"
    fi
done

info "Checking .NET SDK..."
dotnet_version=$(dotnet --version 2>&1) || die ".NET SDK is not installed or not in PATH."
success ".NET SDK $dotnet_version found"

echo -e "  ${GREEN}Phase 1 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 2: Resource Group + ACR
# ============================================================================
phase_header "Phase 2: Resource Group + Container Registry"

info "Creating resource group '$RESOURCE_GROUP_NAME'..."
az group create --name "$RESOURCE_GROUP_NAME" --location "$LOCATION" -o none \
    || die "Failed to create resource group."
success "Resource group ready"

# Check for existing ACR
existing_acr=$(az acr list --resource-group "$RESOURCE_GROUP_NAME" --query "[0].name" -o tsv 2>/dev/null) || true
if [[ -n "$existing_acr" ]]; then
    ACR_NAME="$existing_acr"
    success "Reusing existing ACR: $ACR_NAME"
else
    ACR_NAME="acr${WORKLOAD_NAME}$((RANDOM % 900 + 100))"
    info "Creating ACR '$ACR_NAME'..."
    az acr create --name "$ACR_NAME" --resource-group "$RESOURCE_GROUP_NAME" \
        --sku Basic --admin-enabled true -o none \
        || die "Failed to create ACR."
    success "ACR '$ACR_NAME' created"
fi

info "Ensuring ACR admin user is enabled..."
az acr update --name "$ACR_NAME" --resource-group "$RESOURCE_GROUP_NAME" --admin-enabled true -o none \
    || die "Failed to enable admin user on ACR '$ACR_NAME'."
success "ACR admin user enabled"

# Check for existing Storage Account
existing_storage=$(az storage account list --resource-group "$RESOURCE_GROUP_NAME" --query "[0].name" -o tsv 2>/dev/null) || true
if [[ -n "$existing_storage" ]]; then
    STORAGE_ACCOUNT_NAME="$existing_storage"
    success "Reusing existing Storage Account: $STORAGE_ACCOUNT_NAME"
else
    STORAGE_ACCOUNT_NAME="${storage_prefix}$((RANDOM % 90000 + 10000))"
    info "Storage Account name: $STORAGE_ACCOUNT_NAME (will be created by Bicep)"
fi

# Check for existing AI Services account
existing_ai=$(az cognitiveservices account list --resource-group "$RESOURCE_GROUP_NAME" \
    --query "[?kind=='AIServices'] | [0].name" -o tsv 2>/dev/null) || true
if [[ -n "$existing_ai" ]]; then
    AI_SERVICE_NAME="$existing_ai"
    success "Reusing existing AI Services: $AI_SERVICE_NAME"
else
    AI_SERVICE_NAME="${ai_name_base}-$((RANDOM % 90000 + 10000))"
    info "AI Services name: $AI_SERVICE_NAME (will be created by Bicep)"
fi

echo -e "  ${GREEN}Phase 2 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 3: Entra App Registrations
# ============================================================================
phase_header "Phase 3: Entra App Registrations"

# ── API App ──
info "Creating API app registration 'AIPolicy API'..."
scope_id=""
existing_api_app=$(az_retry ad app list --display-name "AIPolicy API" --query "[0]" -o json 2>/dev/null) || true
if [[ -n "$existing_api_app" && "$existing_api_app" != "null" ]]; then
    api_app_id=$(echo "$existing_api_app" | jq -r '.appId')
    api_obj_id=$(echo "$existing_api_app" | jq -r '.id')
    success "Reusing existing API app: $api_app_id"
else
    api_app_json=$(az_retry ad app create --display-name "AIPolicy API" --sign-in-audience AzureADMultipleOrgs -o json) \
        || die "Failed to create API app."
    api_app_id=$(echo "$api_app_json" | jq -r '.appId')
    api_obj_id=$(echo "$api_app_json" | jq -r '.id')
    success "API app created (multi-tenant): $api_app_id"
fi

# Ensure multi-tenant
az_retry ad app update --id "$api_app_id" --sign-in-audience AzureADMultipleOrgs 2>/dev/null || true

# Add Microsoft Graph openid permission
graph_openid_id="37f7f235-527c-4136-accd-4a02d197296e"
info "Ensuring Microsoft Graph openid permission on API app..."
api_graph_access=$(az_retry ad app show --id "$api_app_id" \
    --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" \
    -o tsv 2>/dev/null) || true
if ! echo "$api_graph_access" | grep -qx "$graph_openid_id"; then
    az_retry ad app permission add --id "$api_app_id" \
        --api 00000003-0000-0000-c000-000000000000 \
        --api-permissions "${graph_openid_id}=Scope" -o none 2>/dev/null || true
fi
success "Graph openid permission configured on API app"

# Set Application ID URI
az_retry ad app update --id "$api_app_id" --identifier-uris "api://$api_app_id" \
    || die "Failed to set API app identifier URI."
success "Identifier URI set: api://$api_app_id"

# Resolve or create API scope
scope_id=$(az_retry ad app show --id "$api_app_id" \
    --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>/dev/null) || true
if [[ -z "$scope_id" ]]; then
    scope_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    scope_body=$(jq -n --arg id "$scope_id" '{
        api: {
            oauth2PermissionScopes: [{
                id: $id,
                adminConsentDisplayName: "Access AIPolicy API",
                adminConsentDescription: "Allows the app to access the AIPolicy API",
                type: "Admin",
                value: "access_as_user",
                isEnabled: true
            }]
        }
    }')
    tmp_scope=$(mktemp)
    echo "$scope_body" > "$tmp_scope"
    az_retry rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
        --headers "Content-Type=application/json" --body "@$tmp_scope" -o none \
        || { rm -f "$tmp_scope"; die "Failed to expose API scope."; }
    rm -f "$tmp_scope"
    success "API scope 'access_as_user' exposed"
else
    success "API scope 'access_as_user' already present"
fi

info "Ensuring API enterprise application exists..."
ensure_service_principal "$api_app_id" "AIPolicy API"

# ── AIPolicy.Export app role ──
info "Ensuring 'AIPolicy.Export' app role..."
existing_export_role=$(az_retry ad app show --id "$api_app_id" \
    --query "appRoles[?value=='AIPolicy.Export'] | [0].id" -o tsv 2>/dev/null) || true
if [[ -z "$existing_export_role" ]]; then
    export_role_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    current_roles=$(az_retry ad app show --id "$api_app_id" --query "appRoles" -o json 2>/dev/null) || true
    [[ -z "$current_roles" || "$current_roles" == "null" ]] && current_roles="[]"
    new_role=$(jq -n --arg id "$export_role_id" '{
        id: $id,
        allowedMemberTypes: ["User","Application"],
        displayName: "AIPolicy Export",
        description: "Allows the user or application to export AI policy data",
        value: "AIPolicy.Export",
        isEnabled: true
    }')
    all_roles=$(echo "$current_roles" | jq --argjson nr "$new_role" '. + [$nr]')
    role_body=$(jq -n --argjson r "$all_roles" '{"appRoles": $r}')
    tmp_role=$(mktemp)
    echo "$role_body" > "$tmp_role"
    az_retry rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
        --headers "Content-Type=application/json" --body "@$tmp_role" -o none \
        || { rm -f "$tmp_role"; die "Failed to add AIPolicy.Export app role."; }
    rm -f "$tmp_role"
    success "'AIPolicy.Export' app role created (ID: $export_role_id)"
else
    export_role_id="$existing_export_role"
    success "'AIPolicy.Export' app role already exists"

    # Ensure allowedMemberTypes includes User
    allowed_types=$(az_retry ad app show --id "$api_app_id" \
        --query "appRoles[?value=='AIPolicy.Export'] | [0].allowedMemberTypes" -o json 2>/dev/null) || true
    if [[ -n "$allowed_types" ]] && ! echo "$allowed_types" | jq -e 'index("User")' >/dev/null 2>&1; then
        info "  Updating app role to allow User assignments..."
        current_roles=$(az_retry ad app show --id "$api_app_id" --query "appRoles" -o json 2>/dev/null)
        updated_roles=$(echo "$current_roles" | jq '
            [.[] | if .value == "AIPolicy.Export"
                   then .allowedMemberTypes = ["User","Application"]
                   else . end]')
        role_body=$(jq -n --argjson r "$updated_roles" '{"appRoles": $r}')
        tmp_role=$(mktemp)
        echo "$role_body" > "$tmp_role"
        az_retry rest --method PATCH \
            --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
            --headers "Content-Type=application/json" --body "@$tmp_role" -o none || true
        rm -f "$tmp_role"
        success "App role updated to allow User + Application"
    fi
fi

# ── AIPolicy.Admin app role ──
info "Ensuring 'AIPolicy.Admin' app role..."
existing_admin_role=$(az_retry ad app show --id "$api_app_id" \
    --query "appRoles[?value=='AIPolicy.Admin'] | [0].id" -o tsv 2>/dev/null) || true
if [[ -z "$existing_admin_role" ]]; then
    admin_role_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    current_roles=$(az_retry ad app show --id "$api_app_id" --query "appRoles" -o json 2>/dev/null) || true
    [[ -z "$current_roles" || "$current_roles" == "null" ]] && current_roles="[]"
    new_role=$(jq -n --arg id "$admin_role_id" '{
        id: $id,
        allowedMemberTypes: ["User","Application"],
        displayName: "AIPolicy Admin",
        description: "Allows the user or application to manage billing plans, client assignments, pricing, and usage policies",
        value: "AIPolicy.Admin",
        isEnabled: true
    }')
    all_roles=$(echo "$current_roles" | jq --argjson nr "$new_role" '. + [$nr]')
    role_body=$(jq -n --argjson r "$all_roles" '{"appRoles": $r}')
    tmp_role=$(mktemp)
    echo "$role_body" > "$tmp_role"
    az_retry rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
        --headers "Content-Type=application/json" --body "@$tmp_role" -o none \
        || { rm -f "$tmp_role"; die "Failed to add AIPolicy.Admin app role."; }
    rm -f "$tmp_role"
    success "'AIPolicy.Admin' app role created (ID: $admin_role_id)"
else
    admin_role_id="$existing_admin_role"
    success "'AIPolicy.Admin' app role already exists"
fi

# ── AIPolicy.Apim app role ──
info "Ensuring 'AIPolicy.Apim' app role..."
existing_apim_role=$(az_retry ad app show --id "$api_app_id" \
    --query "appRoles[?value=='AIPolicy.Apim'] | [0].id" -o tsv 2>/dev/null) || true
if [[ -z "$existing_apim_role" ]]; then
    apim_role_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    current_roles=$(az_retry ad app show --id "$api_app_id" --query "appRoles" -o json 2>/dev/null) || true
    [[ -z "$current_roles" || "$current_roles" == "null" ]] && current_roles="[]"
    new_role=$(jq -n --arg id "$apim_role_id" '{
        id: $id,
        allowedMemberTypes: ["Application"],
        displayName: "APIM Service",
        description: "Allows APIM to call the AIPolicy API precheck and log ingest endpoints",
        value: "AIPolicy.Apim",
        isEnabled: true
    }')
    all_roles=$(echo "$current_roles" | jq --argjson nr "$new_role" '. + [$nr]')
    role_body=$(jq -n --argjson r "$all_roles" '{"appRoles": $r}')
    tmp_role=$(mktemp)
    echo "$role_body" > "$tmp_role"
    az_retry rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
        --headers "Content-Type=application/json" --body "@$tmp_role" -o none \
        || { rm -f "$tmp_role"; die "Failed to add AIPolicy.Apim app role."; }
    rm -f "$tmp_role"
    success "'AIPolicy.Apim' app role created (ID: $apim_role_id)"
else
    apim_role_id="$existing_apim_role"
    success "'AIPolicy.Apim' app role already exists"
fi

# ── Assign app roles to deploying user ──
info "Assigning app roles to deploying user..."
current_user_oid=$(az_retry ad signed-in-user show --query "id" -o tsv 2>/dev/null) || true
if [[ -n "$current_user_oid" ]]; then
    api_sp_id=$(az_retry ad sp show --id "$api_app_id" --query "id" -o tsv 2>/dev/null) || true
    if [[ -n "$api_sp_id" ]]; then
        for role_name_id in "AIPolicy.Export:$export_role_id" "AIPolicy.Admin:$admin_role_id"; do
            role_name="${role_name_id%%:*}"
            role_id="${role_name_id##*:}"
            existing_assignment=$(az_retry rest --method GET \
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
                --query "value[?principalId=='$current_user_oid' && appRoleId=='$role_id'] | [0].id" \
                -o tsv 2>/dev/null) || true
            if [[ -z "$existing_assignment" ]]; then
                assign_body=$(jq -n \
                    --arg pid "$current_user_oid" \
                    --arg rid "$api_sp_id" \
                    --arg aid "$role_id" \
                    '{principalId:$pid,resourceId:$rid,appRoleId:$aid}')
                tmp_assign=$(mktemp)
                echo "$assign_body" > "$tmp_assign"
                if az_retry rest --method POST \
                    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
                    --headers "Content-Type=application/json" --body "@$tmp_assign" -o none 2>/dev/null; then
                    success "$role_name role assigned to current user"
                else
                    warn "Could not assign $role_name — assign manually in Entra ID"
                fi
                rm -f "$tmp_assign"
            else
                success "$role_name role already assigned to current user"
            fi
        done
    fi
else
    warn "Could not determine current user — assign roles manually in Entra ID"
fi

# ── Gateway App (APIM JWT audience) ──
info "Creating gateway app 'AIPolicy APIM Gateway' (multi-tenant)..."
existing_gateway=$(az_retry ad app list --display-name "AIPolicy APIM Gateway" --query "[0]" -o json 2>/dev/null) || true
if [[ -n "$existing_gateway" && "$existing_gateway" != "null" ]]; then
    gateway_app_id=$(echo "$existing_gateway" | jq -r '.appId')
    gateway_obj_id=$(echo "$existing_gateway" | jq -r '.id')
    success "Reusing existing gateway app: $gateway_app_id"
    az_retry ad app update --id "$gateway_app_id" --sign-in-audience AzureADMultipleOrgs 2>/dev/null || true
else
    gateway_json=$(az_retry ad app create --display-name "AIPolicy APIM Gateway" --sign-in-audience AzureADMultipleOrgs -o json) \
        || die "Failed to create gateway app."
    gateway_app_id=$(echo "$gateway_json" | jq -r '.appId')
    gateway_obj_id=$(echo "$gateway_json" | jq -r '.id')
    success "Gateway app created (multi-tenant): $gateway_app_id"
fi

# Set Application ID URI on gateway
az_retry ad app update --id "$gateway_app_id" --identifier-uris "api://$gateway_app_id" \
    || die "Failed to set gateway identifier URI."
success "Gateway identifier URI set: api://$gateway_app_id"

# Graph openid permission on gateway
gateway_graph=$(az_retry ad app show --id "$gateway_app_id" \
    --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" \
    -o tsv 2>/dev/null) || true
if ! echo "$gateway_graph" | grep -qx "$graph_openid_id"; then
    az_retry ad app permission add --id "$gateway_app_id" \
        --api 00000003-0000-0000-c000-000000000000 \
        --api-permissions "${graph_openid_id}=Scope" -o none 2>/dev/null || true
fi

# Expose 'access_as_user' scope on gateway
gateway_scope_id=$(az_retry ad app show --id "$gateway_app_id" \
    --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>/dev/null) || true
if [[ -z "$gateway_scope_id" ]]; then
    gateway_scope_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    gateway_scope_body=$(jq -n --arg id "$gateway_scope_id" '{
        api: {
            oauth2PermissionScopes: [{
                id: $id,
                adminConsentDisplayName: "Access AIPolicy APIM Gateway",
                adminConsentDescription: "Allows the app to call the AIPolicy APIM gateway",
                type: "Admin",
                value: "access_as_user",
                isEnabled: true
            }]
        }
    }')
    tmp_gscope=$(mktemp)
    echo "$gateway_scope_body" > "$tmp_gscope"
    az_retry rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$gateway_obj_id" \
        --headers "Content-Type=application/json" --body "@$tmp_gscope" -o none \
        || { rm -f "$tmp_gscope"; die "Failed to expose gateway scope."; }
    rm -f "$tmp_gscope"
    success "Gateway scope 'access_as_user' exposed"
else
    success "Gateway scope 'access_as_user' already present"
fi

ensure_service_principal "$gateway_app_id" "AIPolicy APIM Gateway"

# ── Client App 1 ──
info "Creating client app 'AIPolicy Sample Client'..."
existing_client1=$(az_retry ad app list --display-name "AIPolicy Sample Client" --query "[0]" -o json 2>/dev/null) || true
if [[ -n "$existing_client1" && "$existing_client1" != "null" ]]; then
    client1_app_id=$(echo "$existing_client1" | jq -r '.appId')
    client1_obj_id=$(echo "$existing_client1" | jq -r '.id')
    success "Reusing existing client app 1: $client1_app_id"
else
    client1_json=$(az_retry ad app create --display-name "AIPolicy Sample Client" --sign-in-audience AzureADMyOrg -o json) \
        || die "Failed to create client app 1."
    client1_app_id=$(echo "$client1_json" | jq -r '.appId')
    client1_obj_id=$(echo "$client1_json" | jq -r '.id')
    success "Client app 1 created: $client1_app_id"
fi

ensure_service_principal "$client1_app_id" "AIPolicy Sample Client"
ensure_delegated_scope_and_consent "$client1_app_id" "$gateway_app_id" "$gateway_scope_id" "AIPolicy Sample Client"

# Graph openid for Client 1
client1_graph=$(az_retry ad app show --id "$client1_app_id" \
    --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" \
    -o tsv 2>/dev/null) || true
if ! echo "$client1_graph" | grep -qx "$graph_openid_id"; then
    az_retry ad app permission add --id "$client1_app_id" \
        --api 00000003-0000-0000-c000-000000000000 \
        --api-permissions "${graph_openid_id}=Scope" -o none 2>/dev/null || true
fi

# Client 1 secret
client1_secret=$(az_retry ad app credential reset --id "$client1_app_id" \
    --display-name "setup-script" --years 1 --query "password" -o tsv 2>/dev/null) || true
[[ -n "$client1_secret" ]] && success "Client 1 secret created"

# Assign AIPolicy.Admin to Client 1 SP
client1_sp_id=$(az_retry ad sp show --id "$client1_app_id" --query "id" -o tsv 2>/dev/null) || true
api_sp_id=$(az_retry ad sp show --id "$api_app_id" --query "id" -o tsv 2>/dev/null) || true
if [[ -n "$client1_sp_id" && -n "$api_sp_id" ]]; then
    existing_admin_assign=$(az_retry rest --method GET \
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
        --query "value[?principalId=='$client1_sp_id' && appRoleId=='$admin_role_id'] | [0].id" \
        -o tsv 2>/dev/null) || true
    if [[ -z "$existing_admin_assign" ]]; then
        assign_body=$(jq -n \
            --arg pid "$client1_sp_id" --arg rid "$api_sp_id" --arg aid "$admin_role_id" \
            '{principalId:$pid,resourceId:$rid,appRoleId:$aid}')
        tmp_assign=$(mktemp)
        echo "$assign_body" > "$tmp_assign"
        az_retry rest --method POST \
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
            --headers "Content-Type=application/json" --body "@$tmp_assign" -o none 2>/dev/null || true
        rm -f "$tmp_assign"
        success "AIPolicy.Admin role assigned to Client 1 SP"
    else
        success "AIPolicy.Admin role already assigned to Client 1 SP"
    fi
fi

# ── Client App 2 (multi-tenant, optional) ──
if [[ "$INCLUDE_EXTERNAL_DEMO_CLIENT" != "true" ]]; then
    info "Skipping 'AIPolicy Demo Client 2' creation (--no-external-demo-client)"
    client2_app_id=""
    client2_obj_id=""
    client2_secret=""
else
info "Creating client app 'AIPolicy Demo Client 2' (multi-tenant)..."
existing_client2=$(az_retry ad app list --display-name "AIPolicy Demo Client 2" --query "[0]" -o json 2>/dev/null) || true
if [[ -n "$existing_client2" && "$existing_client2" != "null" ]]; then
    client2_app_id=$(echo "$existing_client2" | jq -r '.appId')
    client2_obj_id=$(echo "$existing_client2" | jq -r '.id')
    success "Reusing existing client app 2: $client2_app_id"
    az_retry ad app update --id "$client2_app_id" --sign-in-audience AzureADMultipleOrgs 2>/dev/null || true
    success "Client 2 updated to multi-tenant (AzureADMultipleOrgs)"
else
    client2_json=$(az_retry ad app create --display-name "AIPolicy Demo Client 2" --sign-in-audience AzureADMultipleOrgs -o json) \
        || die "Failed to create client app 2."
    client2_app_id=$(echo "$client2_json" | jq -r '.appId')
    client2_obj_id=$(echo "$client2_json" | jq -r '.id')
    success "Client app 2 created (multi-tenant): $client2_app_id"
fi

ensure_service_principal "$client2_app_id" "AIPolicy Demo Client 2"
ensure_delegated_scope_and_consent "$client2_app_id" "$gateway_app_id" "$gateway_scope_id" "AIPolicy Demo Client 2"

# Graph openid for Client 2
client2_graph=$(az_retry ad app show --id "$client2_app_id" \
    --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" \
    -o tsv 2>/dev/null) || true
if ! echo "$client2_graph" | grep -qx "$graph_openid_id"; then
    az_retry ad app permission add --id "$client2_app_id" \
        --api 00000003-0000-0000-c000-000000000000 \
        --api-permissions "${graph_openid_id}=Scope" -o none 2>/dev/null || true
fi

# Public client flow + redirect URI
az_retry ad app update --id "$client2_app_id" \
    --public-client-redirect-uris "http://localhost:29783" \
    --enable-id-token-issuance true 2>/dev/null || true
success "Client 2 public client redirect URI configured (http://localhost:29783)"

# Client 2 secret
client2_secret=$(az_retry ad app credential reset --id "$client2_app_id" \
    --display-name "setup-script" --years 1 --query "password" -o tsv 2>/dev/null) || true
[[ -n "$client2_secret" ]] && success "Client 2 secret created"
fi

echo -e "  ${GREEN}Phase 3 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 4: ACR Image Build
# ============================================================================
phase_header "Phase 4: ACR Image Build"

image_repository="${ACR_NAME}.azurecr.io/aipolicy-api"
run_tag="run-$(date -u +%Y%m%d%H%M%S)"
if [[ "$SKIP_BUILD" == "true" ]]; then
    info "Retrieving latest image tag from ACR '$ACR_NAME'..."
    latest_tag=$(az acr repository show-tags --name "$ACR_NAME" --repository aipolicy-api \
        --orderby time_desc --top 1 --query "[0]" -o tsv 2>/dev/null) || true
    if [[ -n "$latest_tag" ]]; then
        image_tag="${image_repository}:${latest_tag}"
        success "Using latest ACR image: $image_tag"
    else
        die "No images found in ACR repository 'aipolicy-api'. Run without --skip-build first."
    fi
else
    image_tag="${image_repository}:${run_tag}"
fi

if [[ "$SKIP_BUILD" == "true" ]]; then
    echo -e "  ${GRAY}  ⊘ ACR build skipped (--skip-build)${NC}"
    echo ""
else
    info "Writing dashboard auth config for UI build..."
    ui_env_file="$REPO_ROOT/src/aipolicyengine-ui/.env.production.local"
    cat > "$ui_env_file" <<EOF
# Auto-generated by scripts/setup-azure.sh
VITE_AZURE_CLIENT_ID=$api_app_id
VITE_AZURE_TENANT_ID=$tenant_id
VITE_AZURE_API_APP_ID=$api_app_id
VITE_AZURE_AUTHORITY=https://login.microsoftonline.com/$tenant_id
VITE_AZURE_SCOPE=api://$api_app_id/access_as_user
EOF
    success "UI auth config written: $ui_env_file"

    info "Building image in ACR '$ACR_NAME'..."
    info "  Image: aipolicy-api:$run_tag"
    az acr build \
        --image "aipolicy-api:${run_tag}" \
        --resource-group "$RESOURCE_GROUP_NAME" \
        --registry "$ACR_NAME" \
        --file "$REPO_ROOT/src/Dockerfile" \
        "$REPO_ROOT/src/." \
        --no-logs \
        || die "ACR build failed."
    success "Image built and pushed to ACR"

    echo -e "  ${GREEN}Phase 4 complete ✓${NC}"
    echo ""
fi

# ============================================================================
# Phase 5: Bicep Deployment
# ============================================================================
phase_header "Phase 5: Bicep Infrastructure Deployment"

if [[ "$SKIP_BICEP" == "true" ]]; then
    echo -e "  ${GRAY}  ⊘ Bicep deployment skipped (--skip-bicep)${NC}"
    echo ""
else
    info "ACR managed identity pull configured — no admin credentials needed."

    info "Checking soft-deleted resource collisions..."
    deleted_apim_ids=$(az_retry apim deletedservice list --query "[?name=='$APIM_NAME'].id" -o tsv)
    if [[ $? -ne 0 ]]; then
        die "Failed to query soft-deleted APIM services named '$APIM_NAME'."
    fi
    if [[ -n "$deleted_apim_ids" ]]; then
        while IFS= read -r deleted_apim_id; do
            [[ -z "$deleted_apim_id" ]] && continue
            deleted_apim_location=$(printf '%s\n' "$deleted_apim_id" | sed -n 's#^.*/locations/\([^/]*\)/deletedservices/.*#\1#p')
            [[ -n "$deleted_apim_location" ]] || die "Could not determine the location for soft-deleted APIM service '$APIM_NAME' from '$deleted_apim_id'."

            info "  Purging soft-deleted APIM '$APIM_NAME' in '$deleted_apim_location'..."
            purge_output=$(az apim deletedservice purge --service-name "$APIM_NAME" --location "$deleted_apim_location" -o none 2>&1)
            purge_exit=$?

            if [[ $purge_exit -eq 0 ]]; then
                success "Purged APIM soft-delete record in $deleted_apim_location"
            elif grep -qiE 'ServiceNotFound|does not exist' <<<"$purge_output"; then
                success "APIM soft-delete record already absent in $deleted_apim_location"
            else
                printf '%s\n' "$purge_output" >&2
                die "Failed to purge soft-deleted APIM service '$APIM_NAME' in '$deleted_apim_location'."
            fi
        done <<< "$deleted_apim_ids"
    else
        success "No APIM soft-delete collision"
    fi

    deleted_kv=$(az keyvault list-deleted --query "[?name=='$KEY_VAULT_NAME'] | [0].name" -o tsv 2>/dev/null) || true
    if [[ -n "$deleted_kv" ]]; then
        info "  Purging soft-deleted Key Vault '$KEY_VAULT_NAME'..."
        az keyvault purge --name "$KEY_VAULT_NAME" -o none \
            || die "Failed to purge soft-deleted Key Vault '$KEY_VAULT_NAME'."
        success "Purged Key Vault soft-delete record"
    else
        success "No Key Vault soft-delete collision"
    fi

    info "Selecting Azure AI Services account name..."
    existing_ai_in_rg=$(az cognitiveservices account list --resource-group "$RESOURCE_GROUP_NAME" \
        --query "[?kind=='AIServices'] | [0].name" -o tsv 2>/dev/null) || true
    if [[ -n "$existing_ai_in_rg" ]]; then
        AI_SERVICE_NAME="$existing_ai_in_rg"
        success "Reusing existing AI Services: $AI_SERVICE_NAME"
    else
        [[ -z "$AI_SERVICE_NAME" ]] && AI_SERVICE_NAME="${ai_name_base}-$((RANDOM % 90000 + 10000))"
        for attempt in $(seq 1 20); do
            active_collision=$(az cognitiveservices account list \
                --query "[?name=='$AI_SERVICE_NAME'] | [0].name" -o tsv 2>/dev/null) || true
            deleted_collision=$(az cognitiveservices account list-deleted \
                --query "[?name=='$AI_SERVICE_NAME'] | [0].name" -o tsv 2>/dev/null) || true
            if [[ -z "$active_collision" && -z "$deleted_collision" ]]; then
                break
            fi
            echo -e "  ${DARKYELLOW}  Name collision detected for '$AI_SERVICE_NAME' (attempt $attempt) — generating a new name...${NC}"
            AI_SERVICE_NAME="${ai_name_base}-$((RANDOM % 90000 + 10000))"
        done
        if [[ -n "$active_collision" || -n "$deleted_collision" ]]; then
            die "Could not find an available Azure AI Services account name after 20 attempts."
        fi
    fi
    success "Azure AI Services name: $AI_SERVICE_NAME"

    echo -e "  ${MAGENTA}Starting Bicep deployment (this may take 30-60 minutes for APIM)...${NC}"
    info "  Template: infra/bicep/main.bicep"
    bicep_parameter_args=(
        "location=$LOCATION"
        "workloadName=$WORKLOAD_NAME"
        "apimInstanceName=$APIM_NAME"
        "keyVaultName=$KEY_VAULT_NAME"
        "redisCacheName=$REDIS_CACHE_NAME"
        "cosmosAccountName=$COSMOS_ACCOUNT_NAME"
        "logAnalyticsWorkspaceName=$LOG_ANALYTICS_WORKSPACE_NAME"
        "appInsightsName=$APP_INSIGHTS_NAME"
        "storageAccountName=$STORAGE_ACCOUNT_NAME"
        "aiServiceName=$AI_SERVICE_NAME"
        "containerAppName=$CONTAINER_APP_NAME"
        "containerAppEnvName=$CONTAINER_APP_ENV_NAME"
        "containerImage=$image_tag"
        "acrLoginServer=${ACR_NAME}.azurecr.io"
        "acrName=$ACR_NAME"
        "oaiApiName=azure-openai-api"
        "funcApiName=aipolicy-api"
        "enableJwt=$ENABLE_JWT"
        "enableKeys=$ENABLE_KEYS"
    )
    bicep_common_args=(
        --resource-group "$RESOURCE_GROUP_NAME"
        --template-file "$REPO_ROOT/infra/bicep/main.bicep"
        --parameters
        "${bicep_parameter_args[@]}"
        --only-show-errors
        -o json
    )

    info "  Validating Bicep template..."
    bicep_validation_result=$(az deployment group validate "${bicep_common_args[@]}" 2>&1)
    bicep_validation_exit=$?
    if (( bicep_validation_exit != 0 )); then
        if grep -q "The content for this response was already consumed" <<<"$bicep_validation_result"; then
            echo -e "  ${DARKYELLOW}  ⚠ Azure CLI validation bug hit: $bicep_validation_result${NC}"
            echo -e "  ${DARKYELLOW}  Continuing to deployment create so ARM can surface the real error details...${NC}"
        else
            echo -e "  ${RED}Bicep validation error details:${NC}" >&2
            echo "$bicep_validation_result" >&2
            die "Bicep template validation failed. See error output above."
        fi
    else
        success "Bicep template validation passed"
    fi

    bicep_deployment_name="${WORKLOAD_NAME}-infra-$(date -u +%Y%m%d%H%M%S)"
    info "  Deployment name: $bicep_deployment_name"

    bicep_result=$(az deployment group create \
        --name "$bicep_deployment_name" \
        "${bicep_common_args[@]}" 2>&1)
    bicep_exit=$?
    if (( bicep_exit != 0 )); then
        echo -e "  ${RED}Bicep deployment error details:${NC}" >&2
        [[ -n "$bicep_result" ]] && echo "$bicep_result" >&2

        arm_deployment_error=$(az deployment group show \
            --resource-group "$RESOURCE_GROUP_NAME" \
            --name "$bicep_deployment_name" \
            --query "properties.error" -o json 2>/dev/null) || true
        if [[ -n "$arm_deployment_error" && "$arm_deployment_error" != "null" ]]; then
            echo -e "  ${RED}ARM deployment error payload:${NC}" >&2
            echo "$arm_deployment_error" | jq . >&2 2>/dev/null || echo "$arm_deployment_error" >&2
        fi

        failed_operations=$(az deployment operation group list \
            --resource-group "$RESOURCE_GROUP_NAME" \
            --name "$bicep_deployment_name" \
            --query "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceName,statusMessage:properties.statusMessage}" \
            -o json 2>/dev/null) || true
        if [[ -n "$failed_operations" && "$failed_operations" != "[]" && "$failed_operations" != "null" ]]; then
            echo -e "  ${RED}Failed deployment operations:${NC}" >&2
            echo "$failed_operations" | jq . >&2 2>/dev/null || echo "$failed_operations" >&2
        fi

        die "Bicep deployment failed. See error output above."
    fi

    if ! echo "$bicep_result" | jq -e '.' >/dev/null 2>&1; then
        echo -e "  ${RED}Unexpected deployment output:${NC}" >&2
        echo "$bicep_result" >&2
        die "Bicep deployment succeeded but output was not valid JSON."
    fi
    success "Bicep deployment complete"

    container_app_url=$(echo "$bicep_result" | jq -r '.properties.outputs.containerAppUrlInfo.value // empty') || true
    app_insights_conn=$(echo "$bicep_result" | jq -r '.properties.outputs.appInsightsConnectionString.value // empty') || true
    log_analytics_wb_url=$(echo "$bicep_result" | jq -r '.properties.outputs.logAnalyticsWorkbookUrl.value // empty') || true

    echo -e "  ${GREEN}Phase 5 complete ✓${NC}"
    echo ""
fi

# ============================================================================
# Phase 6: Post-Deployment Configuration
# ============================================================================
phase_header "Phase 6: Post-Deployment Configuration"

# Get Container App URL if not already set
if [[ -z "${container_app_url:-}" ]]; then
    info "Retrieving Container App URL..."
    container_app_url=$(az containerapp show --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP_NAME" \
        --query "properties.configuration.ingress.fqdn" -o tsv) \
        || die "Failed to get Container App URL."
fi
success "Container App URL: $container_app_url"

success "Redis uses Entra ID auth (managed identity) — no key required"

# Configure Cosmos DB connection
info "Configuring Cosmos DB connection..."
cosmos_endpoint=$(az cosmosdb show --name "$COSMOS_ACCOUNT_NAME" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --query "documentEndpoint" -o tsv 2>/dev/null) || true
if [[ -n "$cosmos_endpoint" ]]; then
    az containerapp update --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP_NAME" \
        --set-env-vars "ConnectionStrings__aipolicy=$cosmos_endpoint" -o none \
        || die "Failed to update Container App Cosmos connection."
    success "Cosmos DB connection configured: $cosmos_endpoint"
else
    warn "Cosmos DB account not found — skipping connection string"
fi

# Cosmos DB data-plane RBAC
info "Assigning Cosmos DB data contributor role to Container App..."
container_app_principal=$(az containerapp show --name "$CONTAINER_APP_NAME" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --query "identity.principalId" -o tsv 2>/dev/null) || true
cosmos_account_id=$(az cosmosdb show --name "$COSMOS_ACCOUNT_NAME" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --query "id" -o tsv 2>/dev/null) || true

if [[ -z "$container_app_principal" ]]; then
    warn "Container App managed identity not found — cannot assign Cosmos role"
elif [[ -z "$cosmos_account_id" ]]; then
    warn "Cosmos DB account '$COSMOS_ACCOUNT_NAME' not found — cannot assign role"
else
    cosmos_role_def_id="$cosmos_account_id/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
    existing_cosmos_role=$(az cosmosdb sql role assignment list \
        --account-name "$COSMOS_ACCOUNT_NAME" \
        --resource-group "$RESOURCE_GROUP_NAME" \
        --query "[?principalId=='$container_app_principal' && contains(roleDefinitionId, '00000000-0000-0000-0000-000000000002')]" \
        -o tsv 2>/dev/null) || true
    if [[ -z "$existing_cosmos_role" ]]; then
        info "  Creating Cosmos DB role assignment..."
        info "    Principal: $container_app_principal"
        info "    Scope: $cosmos_account_id"
        if az cosmosdb sql role assignment create \
            --account-name "$COSMOS_ACCOUNT_NAME" \
            --resource-group "$RESOURCE_GROUP_NAME" \
            --role-definition-id "$cosmos_role_def_id" \
            --principal-id "$container_app_principal" \
            --scope "$cosmos_account_id" \
            -o none 2>/dev/null; then
            success "Cosmos DB Data Contributor role assigned"
        else
            echo -e "  ${RED}  ✗ Cosmos DB role assignment failed — check permissions${NC}"
            warn "You may need to manually run:"
            echo -e "  ${DARKYELLOW}    az cosmosdb sql role assignment create --account-name $COSMOS_ACCOUNT_NAME --resource-group $RESOURCE_GROUP_NAME --role-definition-id '$cosmos_role_def_id' --principal-id $container_app_principal --scope '$cosmos_account_id'${NC}"
        fi
    else
        success "Cosmos DB Data Contributor role already assigned"
    fi
fi

# AzureAd JWT auth settings
info "Configuring AzureAd JWT auth settings on Container App..."
az containerapp update --name "$CONTAINER_APP_NAME" --resource-group "$RESOURCE_GROUP_NAME" \
    --set-env-vars \
        "AzureAd__Instance=https://login.microsoftonline.com/" \
        "AzureAd__TenantId=$tenant_id" \
        "AzureAd__ClientId=$api_app_id" \
        "AzureAd__Audience=api://$api_app_id" \
    -o none \
    || die "Failed to update Container App AzureAd config."
success "AzureAd JWT auth configured (TenantId, ClientId, Audience)"

# Cognitive Services User role for APIM
info "Assigning Cognitive Services User role to APIM..."
apim_principal=$(az apim show --name "$APIM_NAME" --resource-group "$RESOURCE_GROUP_NAME" \
    --query "identity.principalId" -o tsv) \
    || die "Failed to get APIM principal ID."
ai_svc_id=$(az cognitiveservices account list --resource-group "$RESOURCE_GROUP_NAME" \
    --query "[?kind=='AIServices'].id | [0]" -o tsv 2>/dev/null) || true
if [[ -n "$ai_svc_id" ]]; then
    az role assignment create --assignee "$apim_principal" \
        --role "Cognitive Services User" --scope "$ai_svc_id" -o none 2>/dev/null || true
    success "Cognitive Services User role assigned to APIM"
else
    warn "No AI Services account found — skipping role assignment"
fi

# AIPolicy.Apim role to APIM managed identity
info "Assigning 'AIPolicy.Apim' app role to APIM managed identity..."
api_sp_id=$(az_retry ad sp show --id "$api_app_id" --query "id" -o tsv 2>/dev/null) || true
if [[ -n "$api_sp_id" && -n "$apim_principal" ]]; then
    apim_sp_id=$(az_retry ad sp show --id "$apim_principal" --query "id" -o tsv 2>/dev/null) || true
    [[ -z "$apim_sp_id" ]] && apim_sp_id="$apim_principal"
    existing_apim_assignment=$(az_retry rest --method GET \
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
        --query "value[?principalId=='$apim_sp_id' && appRoleId=='$apim_role_id'] | [0].id" \
        -o tsv 2>/dev/null) || true
    if [[ -z "$existing_apim_assignment" ]]; then
        assign_body=$(jq -n \
            --arg pid "$apim_sp_id" --arg rid "$api_sp_id" --arg aid "$apim_role_id" \
            '{principalId:$pid,resourceId:$rid,appRoleId:$aid}')
        tmp_assign=$(mktemp)
        echo "$assign_body" > "$tmp_assign"
        if az_retry rest --method POST \
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
            --headers "Content-Type=application/json" --body "@$tmp_assign" -o none 2>/dev/null; then
            success "AIPolicy.Apim role assigned to APIM managed identity"
        else
            warn "Could not assign AIPolicy.Apim role to APIM — assign manually"
        fi
        rm -f "$tmp_assign"
    else
        success "AIPolicy.Apim role already assigned to APIM managed identity"
    fi
else
    warn "Could not resolve service principals — assign AIPolicy.Apim role manually"
fi

echo -e "  ${GREEN}Phase 6 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 7: APIM Configuration
# ============================================================================
phase_header "Phase 7: APIM Configuration"

info "Creating APIM named values..."
az apim nv create --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
    --named-value-id EntraTenantId --display-name "EntraTenantId" --value "$tenant_id" -o none 2>/dev/null || true
success "EntraTenantId"

az apim nv create --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
    --named-value-id ExpectedAudience --display-name "ExpectedAudience" --value "api://$gateway_app_id" -o none 2>/dev/null || true
success "ExpectedAudience = api://$gateway_app_id"

az apim nv create --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
    --named-value-id ContainerAppUrl --display-name "ContainerAppUrl" --value "https://$container_app_url" -o none 2>/dev/null || true
success "ContainerAppUrl = https://$container_app_url"

az apim nv create --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
    --named-value-id ContainerAppAudience --display-name "ContainerAppAudience" --value "api://$api_app_id" -o none 2>/dev/null || true
success "ContainerAppAudience = api://$api_app_id"

info "Disabling subscription requirement on OpenAI API..."
if [[ "$ENABLE_JWT" == "true" ]]; then
    az apim api update --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
        --api-id azure-openai-api-jwt --subscription-required false -o none \
        || die "Failed to update API subscription setting."
    success "Subscription requirement disabled"
else
    info "JWT API disabled — skipping subscription requirement update"
fi

if [[ "$ENABLE_JWT" == "true" ]]; then
    info "Configuring JWT-based API path and backend..."
    ai_endpoint=$(az cognitiveservices account list --resource-group "$RESOURCE_GROUP_NAME" \
        --query "[?kind=='AIServices'].properties.endpoint | [0]" -o tsv 2>/dev/null) || true
    if [[ -n "$ai_endpoint" ]]; then
        az apim api update --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
            --api-id azure-openai-api-jwt --set path=jwt/openai --service-url "${ai_endpoint}openai" -o none || true
        success "API path set to /jwt/openai, backend = ${ai_endpoint}openai"
    else
        warn "No AI endpoint found — skipping API path update"
    fi

    info "Uploading APIM JWT validation policy..."
    policy_xml=$(<"$REPO_ROOT/policies/entra-jwt-policy.xml")
    policy_body=$(jq -n --arg xml "$policy_xml" '{"properties":{"format":"rawxml","value":$xml}}')
    tmp_policy=$(mktemp)
    tmp_policy_response=$(mktemp)
    echo "$policy_body" > "$tmp_policy"
    policy_uri="https://management.azure.com/subscriptions/$subscription_id/resourceGroups/$RESOURCE_GROUP_NAME/providers/Microsoft.ApiManagement/service/$APIM_NAME/apis/azure-openai-api-jwt/policies/policy?api-version=2022-08-01"
    az_retry rest --method PUT --uri "$policy_uri" \
        --headers "Content-Type=application/json" --body "@$tmp_policy" --output-file "$tmp_policy_response" -o none \
        || { rm -f "$tmp_policy" "$tmp_policy_response"; die "Failed to upload APIM policy."; }
    rm -f "$tmp_policy" "$tmp_policy_response"
    success "APIM policy uploaded (entra-jwt-policy.xml)"
else
    info "JWT API disabled — skipping JWT policy upload"
fi

if [[ "$ENABLE_KEYS" == "true" ]]; then
    info "Configuring key-based API path and backend..."
    ai_endpoint=$(az cognitiveservices account list --resource-group "$RESOURCE_GROUP_NAME" \
        --query "[?kind=='AIServices'].properties.endpoint | [0]" -o tsv 2>/dev/null) || true
    if [[ -n "$ai_endpoint" ]]; then
        az apim api update --resource-group "$RESOURCE_GROUP_NAME" --service-name "$APIM_NAME" \
            --api-id azure-openai-api-keys --set path=keys/openai --service-url "${ai_endpoint}openai" -o none || true
        success "API path set to /keys/openai, backend = ${ai_endpoint}openai"
    else
        warn "No AI endpoint found — skipping API path update"
    fi

    info "Uploading APIM subscription-key policy..."
    key_policy_xml=$(<"$REPO_ROOT/policies/subscription-key-policy.xml")
    key_policy_body=$(jq -n --arg xml "$key_policy_xml" '{"properties":{"format":"rawxml","value":$xml}}')
    tmp_key_policy=$(mktemp)
    tmp_key_policy_response=$(mktemp)
    echo "$key_policy_body" > "$tmp_key_policy"
    key_policy_uri="https://management.azure.com/subscriptions/$subscription_id/resourceGroups/$RESOURCE_GROUP_NAME/providers/Microsoft.ApiManagement/service/$APIM_NAME/apis/azure-openai-api-keys/policies/policy?api-version=2022-08-01"
    az_retry rest --method PUT --uri "$key_policy_uri" \
        --headers "Content-Type=application/json" --body "@$tmp_key_policy" --output-file "$tmp_key_policy_response" -o none \
        || { rm -f "$tmp_key_policy" "$tmp_key_policy_response"; die "Failed to upload subscription-key APIM policy."; }
    rm -f "$tmp_key_policy" "$tmp_key_policy_response"
    success "APIM policy uploaded (subscription-key-policy.xml)"
else
    info "Key-based API disabled — skipping subscription-key policy upload"
fi

echo -e "  ${GREEN}Phase 7 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 8: Entra Redirect URIs
# ============================================================================
phase_header "Phase 8: Entra Redirect URIs"

info "Setting SPA redirect URIs on API app (used by dashboard UI)..."
redirect_body=$(jq -n \
    --arg u1 "https://$container_app_url" \
    --arg u2 "http://localhost:5173" \
    '{"spa":{"redirectUris":[$u1,$u2]}}')
tmp_redirect=$(mktemp)
echo "$redirect_body" > "$tmp_redirect"
az_retry rest --method PATCH \
    --uri "https://graph.microsoft.com/v1.0/applications/$api_obj_id" \
    --headers "Content-Type=application/json" --body "@$tmp_redirect" -o none \
    || { rm -f "$tmp_redirect"; die "Failed to set redirect URIs on API app."; }
rm -f "$tmp_redirect"
success "API app redirect URIs: https://$container_app_url, http://localhost:5173"

info "Setting SPA redirect URIs on client app 1..."
tmp_redirect=$(mktemp)
echo "$redirect_body" > "$tmp_redirect"
az_retry rest --method PATCH \
    --uri "https://graph.microsoft.com/v1.0/applications/$client1_obj_id" \
    --headers "Content-Type=application/json" --body "@$tmp_redirect" -o none \
    || { rm -f "$tmp_redirect"; die "Failed to set redirect URIs on client app 1."; }
rm -f "$tmp_redirect"
success "Client app 1 redirect URIs: https://$container_app_url, http://localhost:5173"

echo -e "  ${GREEN}Phase 8 complete ✓${NC}"
echo ""

# ============================================================================
# Phase 9: Initial Plan Setup
# ============================================================================
phase_header "Phase 9: Initial Plan Setup"

phase9_failed=false
(
    set -euo pipefail
    base_url="https://$container_app_url"

    # Acquire access token via client credentials
    info "Acquiring access token via client credentials..."
    token_endpoint="https://login.microsoftonline.com/$tenant_id/oauth2/v2.0/token"
    token_response=$(curl -sS -X POST "$token_endpoint" \
        --data-urlencode "grant_type=client_credentials" \
        --data-urlencode "client_id=${client1_app_id}" \
        --data-urlencode "client_secret=${client1_secret}" \
        --data-urlencode "scope=api://${api_app_id}/.default") \
        || { echo "Failed to acquire access token via client credentials." >&2; exit 1; }

    access_token=$(echo "$token_response" | jq -r '.access_token // empty')
    [[ -z "$access_token" ]] && { echo "Token response did not contain an access token." >&2; exit 1; }
    success "Access token acquired"

    # Wait for Container App to be ready
    info "Waiting for Container App to be ready..."
    ready=false
    for attempt in $(seq 1 12); do
        if curl -sf -H "Authorization: Bearer $access_token" \
            "$base_url/api/plans" -o /dev/null --max-time 10 2>/dev/null; then
            ready=true
            break
        fi
        echo -e "  ${GRAY}  Attempt $attempt/12 — waiting 10s...${NC}"
        sleep 10
    done
    [[ "$ready" == "true" ]] || { echo "Container App not responding after 12 attempts." >&2; exit 1; }
    success "Container App is ready"

    plans_json=$(curl -sf -H "Authorization: Bearer $access_token" "$base_url/api/plans" --max-time 15) \
        || { echo "Failed to fetch plans." >&2; exit 1; }

    # ── Enterprise plan ──
    info "Ensuring Enterprise plan..."
    ent_plan_body=$(jq -n '{
        name: "Enterprise",
        monthlyRate: 999.99,
        monthlyTokenQuota: 10000000,
        tokensPerMinuteLimit: 200000,
        requestsPerMinuteLimit: 120,
        allowOverbilling: true,
        costPerMillionTokens: 10.0
    }')
    existing_ent_id=$(echo "$plans_json" | jq -r '[.plans[] | select(.name | ascii_downcase == "enterprise")] | .[0].id // empty')
    if [[ -n "$existing_ent_id" ]]; then
        ent_result=$(curl -sf -X PUT -H "Authorization: Bearer $access_token" \
            -H "Content-Type: application/json" -d "$ent_plan_body" \
            "$base_url/api/plans/$existing_ent_id" --max-time 15)
        ent_plan_id=$(echo "$ent_result" | jq -r '.id')
        success "Enterprise plan updated (ID: $ent_plan_id)"
    else
        ent_result=$(curl -sf -X POST -H "Authorization: Bearer $access_token" \
            -H "Content-Type: application/json" -d "$ent_plan_body" \
            "$base_url/api/plans" --max-time 15)
        ent_plan_id=$(echo "$ent_result" | jq -r '.id')
        success "Enterprise plan created (ID: $ent_plan_id)"
    fi

    # ── Starter plan ──
    info "Ensuring Starter plan..."
    start_plan_body=$(jq -n '{
        name: "Starter",
        monthlyRate: 49.99,
        monthlyTokenQuota: 500,
        tokensPerMinuteLimit: 1000,
        requestsPerMinuteLimit: 10,
        allowOverbilling: false,
        costPerMillionTokens: 0
    }')
    existing_start_id=$(echo "$plans_json" | jq -r '[.plans[] | select(.name | ascii_downcase == "starter")] | .[0].id // empty')
    if [[ -n "$existing_start_id" ]]; then
        start_result=$(curl -sf -X PUT -H "Authorization: Bearer $access_token" \
            -H "Content-Type: application/json" -d "$start_plan_body" \
            "$base_url/api/plans/$existing_start_id" --max-time 15)
        start_plan_id=$(echo "$start_result" | jq -r '.id')
        success "Starter plan updated (ID: $start_plan_id)"
    else
        start_result=$(curl -sf -X POST -H "Authorization: Bearer $access_token" \
            -H "Content-Type: application/json" -d "$start_plan_body" \
            "$base_url/api/plans" --max-time 15)
        start_plan_id=$(echo "$start_result" | jq -r '.id')
        success "Starter plan created (ID: $start_plan_id)"
    fi

    # ── Client assignments ──
    info "Assigning clients to plans..."
    client1_body=$(jq -n --arg pid "$ent_plan_id" '{planId:$pid,displayName:"AIPolicy Sample Client"}')
    curl -sf -X PUT -H "Authorization: Bearer $access_token" \
        -H "Content-Type: application/json" -d "$client1_body" \
        "$base_url/api/clients/$client1_app_id/$tenant_id" -o /dev/null --max-time 15
    success "Client 1 → Enterprise plan (tenant: $tenant_id)"

    if [[ "$INCLUDE_EXTERNAL_DEMO_CLIENT" == "true" && -n "$client2_app_id" ]]; then
        client2_body=$(jq -n --arg pid "$start_plan_id" '{planId:$pid,displayName:"AIPolicy Demo Client 2"}')
        curl -sf -X PUT -H "Authorization: Bearer $access_token" \
            -H "Content-Type: application/json" -d "$client2_body" \
            "$base_url/api/clients/$client2_app_id/$tenant_id" -o /dev/null --max-time 15
        success "Client 2 → Starter plan (tenant: $tenant_id)"

        if [[ -n "$SECONDARY_TENANT_ID" ]]; then
            info "Provisioning service principals in secondary tenant $SECONDARY_TENANT_ID..."
            warn "You must run the following commands while logged into the secondary tenant:"
            echo -e "  ${YELLOW}    az login --tenant $SECONDARY_TENANT_ID${NC}"
            echo -e "  ${YELLOW}    az ad sp create --id $api_app_id${NC}"
            echo -e "  ${YELLOW}    az ad sp create --id $gateway_app_id${NC}"
            echo -e "  ${YELLOW}    az ad sp create --id $client2_app_id${NC}"
            echo -e "  ${YELLOW}    az login --tenant $tenant_id   # switch back${NC}"

            client2_sec_body=$(jq -n --arg pid "$start_plan_id" '{planId:$pid,displayName:"AIPolicy Demo Client 2 (Secondary Tenant)"}')
            curl -sf -X PUT -H "Authorization: Bearer $access_token" \
                -H "Content-Type: application/json" -d "$client2_sec_body" \
                "$base_url/api/clients/$client2_app_id/$SECONDARY_TENANT_ID" -o /dev/null --max-time 15
            success "Client 2 → Starter plan (secondary tenant: $SECONDARY_TENANT_ID)"
        fi
    else
        info "Skipping Client 2 plan registration (--no-external-demo-client)"
    fi

    # Write plan IDs to temp files so the parent shell can pick them up
    echo "$ent_plan_id"  > /tmp/.setup_ent_plan_id
    echo "$start_plan_id" > /tmp/.setup_start_plan_id

    echo -e "  ${GREEN}Phase 9 complete ✓${NC}"
    echo ""
) || {
    phase9_failed=true
    echo -e "  ${RED}✗ Phase 9 failed${NC}"
    echo -e "  ${DARKYELLOW}  You can manually create plans via the dashboard at https://$container_app_url${NC}"
    echo ""
}

# Pick up plan IDs from subshell
enterprise_plan_id=""
starter_plan_id=""
if [[ -f /tmp/.setup_ent_plan_id ]]; then
    enterprise_plan_id=$(<"/tmp/.setup_ent_plan_id")
    rm -f /tmp/.setup_ent_plan_id
fi
if [[ -f /tmp/.setup_start_plan_id ]]; then
    starter_plan_id=$(<"/tmp/.setup_start_plan_id")
    rm -f /tmp/.setup_start_plan_id
fi

# ============================================================================
# Phase 10: Summary Output
# ============================================================================
phase_header "Phase 10: Deployment Summary"

client1_secret_env="${client1_secret:-<client-1-secret>}"
client2_secret_env="${client2_secret:-<client-2-secret>}"

# Write demo env file
demo_env_file="$REPO_ROOT/demo/.env.local"
cat > "$demo_env_file" <<EOF
# Auto-generated by scripts/setup-azure.sh
# Update deployment IDs if your Azure OpenAI deployment names differ.
DemoClient__TenantId=$tenant_id
DemoClient__SecondaryTenantId=${SECONDARY_TENANT_ID}
DemoClient__ApiScope=api://$gateway_app_id/.default
DemoClient__ApimBase=https://${APIM_NAME}.azure-api.net/jwt
DemoClient__ApiVersion=2024-02-01
DemoClient__AIPolicyBase=https://$container_app_url
DemoClient__Clients__0__Name="AIPolicy Sample Client"
DemoClient__Clients__0__AppId=$client1_app_id
DemoClient__Clients__0__Secret=$client1_secret_env
DemoClient__Clients__0__Plan=Enterprise
DemoClient__Clients__0__DeploymentId=gpt-4o
DemoClient__Clients__0__TenantId=$tenant_id
EOF

if [[ "$INCLUDE_EXTERNAL_DEMO_CLIENT" == "true" && -n "$client2_app_id" ]]; then
    cat >> "$demo_env_file" <<EOF
DemoClient__Clients__1__Name="AIPolicy Demo Client 2"
DemoClient__Clients__1__AppId=$client2_app_id
DemoClient__Clients__1__Secret=$client2_secret_env
DemoClient__Clients__1__Plan=Starter
DemoClient__Clients__1__DeploymentId=gpt-4o-mini
DemoClient__Clients__1__TenantId=$tenant_id
EOF
fi

cat >> "$demo_env_file" <<EOF
DemoClient__AgentInstructions="You are a concise Azure platform assistant. Keep responses to one sentence."
DemoClient__Prompts__0="What is Azure API Management in one sentence?"
DemoClient__Prompts__1="Explain token-based billing in one sentence."
EOF

# Write deployment-output.json
output_file="$REPO_ROOT/deployment-output.json"
jq -n \
    --arg subscriptionId "$subscription_id" \
    --arg tenantId "$tenant_id" \
    --arg acrName "$ACR_NAME" \
    --arg resourceGroupName "$RESOURCE_GROUP_NAME" \
    --arg apimName "$APIM_NAME" \
    --arg containerAppName "$CONTAINER_APP_NAME" \
    --arg containerAppEnvName "$CONTAINER_APP_ENV_NAME" \
    --arg redisCacheName "$REDIS_CACHE_NAME" \
    --arg cosmosAccountName "$COSMOS_ACCOUNT_NAME" \
    --arg keyVaultName "$KEY_VAULT_NAME" \
    --arg logAnalyticsWorkspaceName "$LOG_ANALYTICS_WORKSPACE_NAME" \
    --arg appInsightsName "$APP_INSIGHTS_NAME" \
    --arg aiServiceName "$AI_SERVICE_NAME" \
    --arg storageAccountName "$STORAGE_ACCOUNT_NAME" \
    --arg containerImage "$image_tag" \
    --arg containerAppUrl "${container_app_url:-}" \
    --arg appInsightsConnectionString "${app_insights_conn:-}" \
    --arg logAnalyticsWorkbookUrl "${log_analytics_wb_url:-}" \
    --arg dashboardUiEnvFile "${ui_env_file:-}" \
    --arg demoClientEnvFile "$demo_env_file" \
    --arg apiAppId "$api_app_id" \
    --arg apiObjId "$api_obj_id" \
    --arg gatewayAppId "$gateway_app_id" \
    --arg gatewayObjId "$gateway_obj_id" \
    --arg adminRoleId "$admin_role_id" \
    --arg client1AppId "$client1_app_id" \
    --arg client1ObjId "$client1_obj_id" \
    --arg client1Secret "${client1_secret:-}" \
    --arg client2AppId "$client2_app_id" \
    --arg client2ObjId "$client2_obj_id" \
    --arg client2Secret "${client2_secret:-}" \
    --arg enterprisePlanId "${enterprise_plan_id:-}" \
    --arg starterPlanId "${starter_plan_id:-}" \
    '$ARGS.named' > "$output_file"

info "Deployment output written to: $output_file"
echo ""

echo -e "${GREEN}╔══════════════════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║   Deployment Complete!                                   ║${NC}"
echo -e "${GREEN}╚══════════════════════════════════════════════════════════╝${NC}"
echo ""
echo -e "  ${CYAN}── Azure Resources ──${NC}"
echo "  Resource Group:    $RESOURCE_GROUP_NAME"
echo "  ACR:               $ACR_NAME"
echo "  APIM:              $APIM_NAME"
echo "  Container App:     $CONTAINER_APP_NAME"
echo "  Container Env:     $CONTAINER_APP_ENV_NAME"
echo "  Redis:             $REDIS_CACHE_NAME"
echo "  Cosmos DB:         $COSMOS_ACCOUNT_NAME"
echo "  AI Services:       $AI_SERVICE_NAME"
echo "  Key Vault:         $KEY_VAULT_NAME"
echo "  Log Analytics:     $LOG_ANALYTICS_WORKSPACE_NAME"
echo "  App Insights:      $APP_INSIGHTS_NAME"
echo "  Storage Account:   $STORAGE_ACCOUNT_NAME"
echo ""
echo -e "  ${CYAN}── URLs ──${NC}"
echo "  Dashboard:         https://$container_app_url"
echo "  APIM Gateway:      https://${APIM_NAME}.azure-api.net"
[[ -n "${log_analytics_wb_url:-}" ]] && echo "  Log Analytics WB:  $log_analytics_wb_url"
echo ""
echo -e "  ${CYAN}── Entra App Registrations ──${NC}"
echo "  API App ID:        $api_app_id"
echo "  API Audience:      api://$api_app_id   (dashboard UI → Container App, plan seeding)"
echo "  Gateway App ID:    $gateway_app_id"
echo "  Gateway Audience:  api://$gateway_app_id   (client → APIM — used by demo DemoClient__ApiScope)"
echo "  Client 1 App ID:   $client1_app_id"
[[ -n "${client1_secret:-}" ]] && echo -e "  Client 1 Secret:   ${DARKYELLOW}$client1_secret${NC}"
if [[ "$INCLUDE_EXTERNAL_DEMO_CLIENT" == "true" && -n "$client2_app_id" ]]; then
    echo "  Client 2 App ID:   $client2_app_id"
    [[ -n "${client2_secret:-}" ]] && echo -e "  Client 2 Secret:   ${DARKYELLOW}$client2_secret${NC}"
else
    echo -e "  Client 2:          ${GRAY}(skipped — --no-external-demo-client)${NC}"
fi
echo ""
echo -e "  ${DARKYELLOW}⚠ Token audience changed: clients going through APIM must request${NC}"
echo -e "  ${DARKYELLOW}  api://$gateway_app_id/.default. Cached tokens targeting the old API${NC}"
echo -e "  ${DARKYELLOW}  audience will receive 401 from APIM until refreshed.${NC}"
echo ""
if [[ -n "${ui_env_file:-}" ]]; then
    echo -e "  ${CYAN}── Dashboard UI Auth Config ──${NC}"
    echo "  Env file:          $ui_env_file"
    echo "  Contains:          VITE_AZURE_CLIENT_ID, VITE_AZURE_TENANT_ID, VITE_AZURE_SCOPE"
    echo ""
fi
echo -e "  ${CYAN}── DemoClient Config ──${NC}"
echo "  Env file:          $demo_env_file"
echo "  Sample template:   demo/.env.sample"
echo "  Run DemoClient:    dotnet run --project demo"
echo ""
echo -e "  ${CYAN}── Next Steps ──${NC}"
echo "  1. Open the dashboard: https://$container_app_url"
echo "  2. Test the APIM endpoint with a Bearer token"
echo "  3. Check APIM policy is applied: Azure Portal → APIM → APIs → azure-openai-api"
echo "  4. Review deployment-output.json for all resource IDs"
echo ""
echo -e "  ${DARKYELLOW}⚠  Client secrets are shown above — save them securely!${NC}"
echo ""
