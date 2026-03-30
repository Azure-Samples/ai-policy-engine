#!/usr/bin/env bash
#
# Builds the Chargeback API container image and deploys it to the provisioned infrastructure.
#
# Post-deploy script to run after Terraform (or Bicep) has provisioned the infrastructure.
# Builds the Docker image (multi-stage: Node.js UI + .NET API) via ACR Tasks,
# and updates the Container App to use the new image.
#
# This two-stage approach is required because:
#   1. Terraform deploys infrastructure with a placeholder image (mcr.microsoft.com/dotnet/aspnet:10.0)
#   2. This script builds the real image in ACR and updates the Container App
#
# This is the standard pattern for enterprise environments where public container registries are disabled.
#
# Usage:
#   ./deploy-container.sh -g <resource-group> [-w <workload-name>] [-t <tag>] [-s]
#
# Examples:
#   ./deploy-container.sh -g rg-chrgbk-eastus2
#   ./deploy-container.sh -g rg-chrgbk-eastus2 -w chrgbk -t v1.0.0
#   ./deploy-container.sh -g rg-chrgbk-eastus2 -s   # skip build

set -euo pipefail

# ── Defaults ─────────────────────────────────────────────────────────

RESOURCE_GROUP_NAME=""
WORKLOAD_NAME="chrgbk"
TAG=""
SKIP_BUILD=false

# ── Parse arguments ──────────────────────────────────────────────────

usage() {
    echo "Usage: $0 -g <resource-group> [-w <workload-name>] [-t <tag>] [-s]"
    echo "  -g  Resource group containing the deployed infrastructure (required)"
    echo "  -w  Workload name prefix (default: chrgbk)"
    echo "  -t  Image tag (default: timestamped run-YYYYMMDDHHMMSS)"
    echo "  -s  Skip the build step"
    exit 1
}

while getopts "g:w:t:sh" opt; do
    case $opt in
        g) RESOURCE_GROUP_NAME="$OPTARG" ;;
        w) WORKLOAD_NAME="$OPTARG" ;;
        t) TAG="$OPTARG" ;;
        s) SKIP_BUILD=true ;;
        h) usage ;;
        *) usage ;;
    esac
done

if [[ -z "$RESOURCE_GROUP_NAME" ]]; then
    echo "Error: -g <resource-group> is required."
    usage
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── Color helpers ────────────────────────────────────────────────────

cyan()       { printf '\033[0;36m%s\033[0m\n' "$*"; }
green()      { printf '\033[0;32m%s\033[0m\n' "$*"; }
yellow()     { printf '\033[0;33m%s\033[0m\n' "$*"; }
dark_yellow() { printf '\033[0;33m%s\033[0m\n' "$*"; }
dark_gray()  { printf '\033[0;90m%s\033[0m\n' "$*"; }
die()        { printf '\033[0;31mError: %s\033[0m\n' "$*" >&2; exit 1; }

echo ""
cyan "╔══════════════════════════════════════════════════════════╗"
cyan "║  Chargeback API — Container Build & Deploy              ║"
cyan "╚══════════════════════════════════════════════════════════╝"
echo ""

# ── Step 1: Discover infrastructure ──────────────────────────────────

yellow "  Step 1: Discovering infrastructure..."

acr_name=$(az acr list --resource-group "$RESOURCE_GROUP_NAME" --query "[0].name" -o tsv)
[[ -z "$acr_name" ]] && die "No ACR found in resource group $RESOURCE_GROUP_NAME"
green "    ACR: $acr_name"

container_app_name=$(az containerapp list --resource-group "$RESOURCE_GROUP_NAME" --query "[0].name" -o tsv)
[[ -z "$container_app_name" ]] && die "No Container App found in resource group $RESOURCE_GROUP_NAME"
green "    Container App: $container_app_name"

acr_login_server=$(az acr show --name "$acr_name" --query "loginServer" -o tsv)
green "    ACR Login Server: $acr_login_server"

# Resolve infrastructure details — prefer Terraform outputs when available,
# fall back to Azure CLI queries for standalone usage.
tf_dir="$REPO_ROOT/infra/terraform"
tenant_id=""
api_app_id=""
container_app_fqdn=""

if [[ -f "$tf_dir/terraform.tfstate" ]]; then
    pushd "$tf_dir" > /dev/null
    tenant_id=$(terraform output -raw tenant_id 2>/dev/null || true)
    api_app_id=$(terraform output -raw api_app_id 2>/dev/null || true)
    container_app_fqdn=$(terraform output -raw container_app_url 2>/dev/null | sed 's|^https://||' || true)
    popd > /dev/null
fi

[[ -z "$tenant_id" ]] && tenant_id=$(az account show --query "tenantId" -o tsv)
[[ -z "$container_app_fqdn" ]] && container_app_fqdn=$(az containerapp show \
    --name "$container_app_name" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --query "properties.configuration.ingress.fqdn" -o tsv)
[[ -z "$api_app_id" ]] && api_app_id=$(az ad app list --display-name "Chargeback API" --query "[0].appId" -o tsv)

green "    Tenant: $tenant_id"
green "    API App: $api_app_id"
green "    Container App FQDN: $container_app_fqdn"

# ── Step 1b: Ensure Entra app config is correct ──────────────────────
# The AzureAD Terraform provider has eventual-consistency issues where
# identifier URIs and redirect URIs silently fail. This step guarantees
# all Entra app config is correct before we build the container image
# (which bakes the UI env file into the image).

echo ""
yellow "  Step 1b: Verifying Entra ID app configuration..."

graph_token=$(az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv)

# ── API App: identifier URI ──
current_uris=$(az ad app show --id "$api_app_id" --query "identifierUris[]" -o tsv)
if ! echo "$current_uris" | grep -qF "api://$api_app_id"; then
    dark_yellow "    ⚠ API identifier URI missing — adding api://$api_app_id"
    az ad app update --id "$api_app_id" --identifier-uris "api://$api_app_id"
else
    green "    ✓ API identifier URI: api://$api_app_id"
fi

# ── API App: SPA redirect URIs ──
current_spa=$(az ad app show --id "$api_app_id" --query "spa.redirectUris[]" -o tsv)
required_uris=("https://$container_app_fqdn" "http://localhost:5173")
missing=()
for uri in "${required_uris[@]}"; do
    if ! echo "$current_spa" | grep -qF "$uri"; then
        missing+=("$uri")
    fi
done

if [[ ${#missing[@]} -gt 0 ]]; then
    dark_yellow "    ⚠ SPA redirect URIs missing — setting..."
    # Merge existing + required, deduplicate
    all_uris=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && all_uris+=("$line")
    done <<< "$current_spa"
    for uri in "${required_uris[@]}"; do
        if ! printf '%s\n' "${all_uris[@]}" | grep -qF "$uri"; then
            all_uris+=("$uri")
        fi
    done

    obj_id=$(az ad app show --id "$api_app_id" --query "id" -o tsv)
    # Build JSON array of URIs
    uri_json=$(printf '%s\n' "${all_uris[@]}" | jq -R . | jq -s .)
    body=$(jq -n --argjson uris "$uri_json" '{"spa":{"redirectUris":$uris}}')
    curl -s -X PATCH \
        "https://graph.microsoft.com/v1.0/applications/$obj_id" \
        -H "Authorization: Bearer $graph_token" \
        -H "Content-Type: application/json" \
        -d "$body" > /dev/null
    green "    ✓ SPA redirect URIs configured"
else
    green "    ✓ SPA redirect URIs OK"
fi

# ── Gateway App: identifier URI ──
gateway_app_id=""
if [[ -f "$tf_dir/terraform.tfstate" ]]; then
    pushd "$tf_dir" > /dev/null
    gateway_app_id=$(terraform output -raw gateway_app_id 2>/dev/null || true)
    popd > /dev/null
fi

if [[ -n "$gateway_app_id" ]]; then
    gw_uris=$(az ad app show --id "$gateway_app_id" --query "identifierUris[]" -o tsv)
    if ! echo "$gw_uris" | grep -qF "api://$gateway_app_id"; then
        dark_yellow "    ⚠ Gateway identifier URI missing — adding api://$gateway_app_id"
        az ad app update --id "$gateway_app_id" --identifier-uris "api://$gateway_app_id"
    else
        green "    ✓ Gateway identifier URI: api://$gateway_app_id"
    fi
else
    dark_gray "    ⊘ Gateway app ID not found — skipping"
fi

# ── API App: service principal exists ──
api_sp_id=$(az ad sp list --filter "appId eq '$api_app_id'" --query "[0].id" -o tsv)
if [[ -z "$api_sp_id" ]]; then
    dark_yellow "    ⚠ API service principal missing — creating..."
    az ad sp create --id "$api_app_id" -o none
    api_sp_id=$(az ad sp list --filter "appId eq '$api_app_id'" --query "[0].id" -o tsv)
    green "    ✓ API service principal created"
else
    green "    ✓ API service principal exists"
fi

# ── Gateway App: service principal exists ──
if [[ -n "$gateway_app_id" ]]; then
    gw_sp_id=$(az ad sp list --filter "appId eq '$gateway_app_id'" --query "[0].id" -o tsv)
    if [[ -z "$gw_sp_id" ]]; then
        dark_yellow "    ⚠ Gateway service principal missing — creating..."
        az ad sp create --id "$gateway_app_id" -o none
        green "    ✓ Gateway service principal created"
    else
        green "    ✓ Gateway service principal exists"
    fi
fi

# ── Deploying user: ensure Admin and Export roles ──
echo ""
yellow "  Step 1c: Ensuring deployer has Admin and Export roles..."

current_user_id=$(az ad signed-in-user show --query "id" -o tsv)
app_roles_json=$(az ad app show --id "$api_app_id" --query "appRoles[].{id:id,value:value}" -o json)
admin_role_id=$(echo "$app_roles_json" | jq -r '.[] | select(.value == "Chargeback.Admin") | .id')
export_role_id=$(echo "$app_roles_json" | jq -r '.[] | select(.value == "Chargeback.Export") | .id')

existing_assignments=$(az rest --method GET \
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" 2>/dev/null || echo '{"value":[]}')

assign_role() {
    local role_id="$1"
    local role_name="$2"

    if [[ -z "$role_id" ]]; then
        dark_gray "    ⊘ $role_name role not found on app — skipping"
        return
    fi

    has_role=$(echo "$existing_assignments" | jq -r \
        --arg uid "$current_user_id" --arg rid "$role_id" \
        '.value[] | select(.principalId == $uid and .appRoleId == $rid) | .id')

    if [[ -n "$has_role" ]]; then
        green "    ✓ $role_name role assigned"
    else
        dark_yellow "    ⚠ $role_name role missing — assigning..."
        body=$(jq -n \
            --arg pid "$current_user_id" \
            --arg rid "$api_sp_id" \
            --arg arid "$role_id" \
            '{"principalId":$pid,"resourceId":$rid,"appRoleId":$arid}')
        if az rest --method POST \
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$api_sp_id/appRoleAssignedTo" \
            --headers "Content-Type=application/json" \
            --body "$body" -o none 2>/dev/null; then
            green "    ✓ $role_name role assigned"
        else
            dark_yellow "    ⚠ $role_name role assignment failed (may already exist)"
        fi
    fi
}

assign_role "$admin_role_id" "Chargeback.Admin"
assign_role "$export_role_id" "Chargeback.Export"

# ── Step 2: Write UI auth config ─────────────────────────────────────

echo ""
yellow "  Step 2: Writing dashboard auth config..."

ui_env_file="$REPO_ROOT/src/chargeback-ui/.env.production.local"
cat > "$ui_env_file" <<EOF
VITE_AZURE_TENANT_ID=$tenant_id
VITE_AZURE_CLIENT_ID=$api_app_id
VITE_AZURE_API_APP_ID=$api_app_id
VITE_AZURE_SCOPE=api://$api_app_id/access_as_user
VITE_API_URL=https://$container_app_fqdn
EOF
green "    ✓ Wrote $ui_env_file"

# ── Step 3: Build & push image via ACR Tasks ─────────────────────────

image_name="chargeback-api"
if [[ -z "$TAG" ]]; then
    TAG="run-$(date -u +'%Y%m%d%H%M%S')"
fi
image_tag="$acr_login_server/$image_name:$TAG"

echo ""
yellow "  Step 3: Building & pushing container image via ACR Tasks..."
echo "    Image: $image_tag"

if [[ "$SKIP_BUILD" == true ]]; then
    dark_gray "    ⊘ Build skipped (-s)"
else
    az acr build \
        --image "$image_name:$TAG" \
        --resource-group "$RESOURCE_GROUP_NAME" \
        --registry "$acr_name" \
        --file "$REPO_ROOT/src/Dockerfile" \
        "$REPO_ROOT/src/." \
        --no-logs

    # Tag the same image as latest (no rebuild)
    az acr import \
        --name "$acr_name" \
        --source "$acr_login_server/$image_name:$TAG" \
        --image "$image_name:latest" \
        --force

    green "    ✓ Image built & pushed to $acr_login_server"
fi

# ── Step 4: Update Container App ─────────────────────────────────────

echo ""
yellow "  Step 4: Updating Container App..."

az containerapp update \
    --name "$container_app_name" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --image "$image_tag" \
    -o none

new_fqdn=$(az containerapp show \
    --name "$container_app_name" \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --query "properties.configuration.ingress.fqdn" -o tsv)
green "    ✓ Container App updated"
cyan  "    URL: https://$new_fqdn"

# ── Done ─────────────────────────────────────────────────────────────

echo ""
green "  ✓ Deployment complete!"
echo ""
cyan  "  Container App: https://$new_fqdn"
cyan  "  Image:         $image_tag"
echo ""
