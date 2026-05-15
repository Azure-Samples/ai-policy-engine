#!/usr/bin/env bash
# Post-provisioning script for Azure AI Policy Engine
# Called by azd after infrastructure provisioning completes. Runs two steps:
#   1. Cosmos DB data-plane RBAC for the Container App's managed identity
#      (Cosmos RBAC must be assigned via az CLI — Terraform azurerm provider can't do this for serverless accounts).
#   2. Registering the Container App's FQDN as a SPA redirect URI on the
#      API Entra app so MSAL.js auth-code flow succeeds (avoids AADSTS50011).

set -euo pipefail

get_azd_env() {
    azd env get-values 2>/dev/null | grep "^$1=" | sed "s/^$1=//" | tr -d '"' || true
}

echo "=== Post-provisioning: Configuring Cosmos DB data-plane RBAC ==="

RESOURCE_GROUP=$(get_azd_env AZURE_RESOURCE_GROUP)
if [ -z "${RESOURCE_GROUP:-}" ]; then
    echo "Skipping: AZURE_RESOURCE_GROUP not set"
    exit 0
fi

# Fetch all container apps once, filter in shell — avoids JMESPath quote-escaping
# fragility and lets us reuse the JSON for both the principal id, cosmos account
# resolution, and the ingress FQDN below.
ALL_CONTAINER_APPS_JSON=$(az containerapp list --resource-group "$RESOURCE_GROUP" -o json 2>/dev/null || echo "[]")
API_CONTAINER_APP_JSON=$(echo "$ALL_CONTAINER_APPS_JSON" | python3 -c "
import json, sys
apps = json.load(sys.stdin)
for a in apps:
    if (a.get('tags') or {}).get('azd-service-name') == 'api':
        print(json.dumps(a))
        break
" 2>/dev/null || echo "")

if [ -z "${API_CONTAINER_APP_JSON:-}" ]; then
    echo "Skipping: No container app found with azd-service-name=api tag"
    exit 0
fi

PRINCIPAL_ID=$(echo "$API_CONTAINER_APP_JSON" | python3 -c "import json,sys;print(json.load(sys.stdin).get('identity',{}).get('principalId',''))" 2>/dev/null || true)

# Prefer deriving the Cosmos account name from the Container App's
# ConnectionStrings__aipolicy env var (mirrors what the API actually uses);
# fall back to the first Cosmos account in the RG if that's not present.
COSMOS_ACCOUNT=$(echo "$API_CONTAINER_APP_JSON" | python3 -c "
import json, sys
from urllib.parse import urlparse
app = json.load(sys.stdin)
for c in (app.get('properties',{}).get('template',{}).get('containers') or []):
    for e in (c.get('env') or []):
        if e.get('name') == 'ConnectionStrings__aipolicy' and e.get('value'):
            host = urlparse(e['value']).hostname or ''
            print(host.split('.')[0])
            sys.exit(0)
" 2>/dev/null || true)

if [ -z "${COSMOS_ACCOUNT:-}" ]; then
    COSMOS_ACCOUNT=$(az cosmosdb list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || true)
fi

if [ -z "${COSMOS_ACCOUNT:-}" ] || [ -z "${PRINCIPAL_ID:-}" ]; then
    echo "Skipping Cosmos RBAC: could not resolve account name or principal ID"
else
    echo "Cosmos Account: $COSMOS_ACCOUNT"
    echo "Principal ID:   $PRINCIPAL_ID"

    # Built-in "Cosmos DB Built-in Data Contributor" role
    ROLE_DEFINITION_ID="00000000-0000-0000-0000-000000000002"

    EXISTING=$(az cosmosdb sql role assignment list \
        --account-name "$COSMOS_ACCOUNT" \
        --resource-group "$RESOURCE_GROUP" \
        --query "[?principalId=='$PRINCIPAL_ID' && contains(roleDefinitionId, '$ROLE_DEFINITION_ID')] | length(@)" \
        -o tsv 2>/dev/null || echo "0")

    if [ "$EXISTING" -gt 0 ]; then
        echo "Cosmos DB RBAC already configured — skipping."
    else
        echo "Assigning Cosmos DB Data Contributor role..."
        az cosmosdb sql role assignment create \
            --account-name "$COSMOS_ACCOUNT" \
            --resource-group "$RESOURCE_GROUP" \
            --role-definition-id "$ROLE_DEFINITION_ID" \
            --principal-id "$PRINCIPAL_ID" \
            --scope "/" \
            -o none
        echo "Cosmos DB RBAC configured successfully."
    fi
fi

# ---------------------------------------------------------------------------
# Register the Container App FQDN as a SPA redirect URI on the API Entra app.
# Required so MSAL.js in the dashboard can complete the auth-code flow against
# https://<container-app-fqdn>. Without this, login fails with AADSTS500113.
# ---------------------------------------------------------------------------
echo ""
echo "=== Post-provisioning: Registering SPA redirect URI on API Entra app ==="

# Use the Terraform-managed api_app_id (not the legacy CONTAINER_APP_CLIENT_ID)
APP_ID=$(get_azd_env api_app_id)
FQDN=$(echo "$API_CONTAINER_APP_JSON" | python3 -c "import json,sys;print(json.load(sys.stdin).get('properties',{}).get('configuration',{}).get('ingress',{}).get('fqdn',''))" 2>/dev/null || true)

if [ -z "${APP_ID:-}" ]; then
    echo "Skipping: api_app_id not set in azd env."
elif [ -z "${FQDN:-}" ]; then
    echo "Skipping: container app has no ingress FQDN."
else
    REDIRECT_URI="https://$FQDN"
    echo "Container App FQDN: $FQDN"
    echo "Redirect URI:       $REDIRECT_URI"

    OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)
    if [ -z "${OBJECT_ID:-}" ]; then
        echo "ERROR: Could not resolve object id for app $APP_ID."
    else
        EXISTING_URIS_JSON=$(az rest --method GET \
            --uri "https://graph.microsoft.com/v1.0/applications/$OBJECT_ID" \
            --query "spa.redirectUris" -o json 2>/dev/null || echo "[]")

        ALREADY_REGISTERED=$(echo "$EXISTING_URIS_JSON" | python3 -c "
import json, sys
existing = json.load(sys.stdin) or []
print('yes' if '$REDIRECT_URI' in existing else 'no')
" 2>/dev/null || echo "no")

        if [ "$ALREADY_REGISTERED" = "yes" ]; then
            echo "Redirect URI already registered — skipping."
        else
            BODY=$(echo "$EXISTING_URIS_JSON" | python3 -c "
import json, sys
existing = json.load(sys.stdin) or []
new_uri = '$REDIRECT_URI'
if new_uri not in existing:
    existing.append(new_uri)
print(json.dumps({'spa': {'redirectUris': existing}}))
" 2>/dev/null || echo "")
            if [ -z "$BODY" ]; then
                echo "  ✗ python3 not available — cannot serialize redirect URI body. Install python3 or register the URI manually."
            else
                TMP=$(mktemp)
                echo "$BODY" > "$TMP"
                if az rest --method PATCH \
                    --uri "https://graph.microsoft.com/v1.0/applications/$OBJECT_ID" \
                    --headers "Content-Type=application/json" \
                    --body "@$TMP" -o none 2>/dev/null; then
                    echo "  ✓ Registered $REDIRECT_URI as SPA redirect URI."
                else
                    echo "  ✗ Failed to register redirect URI (continuing)."
                fi
                rm -f "$TMP"
            fi
        fi
    fi
fi

# ---------------------------------------------------------------------------
# Assign the deploying user to the AIPolicy.Admin app role.
# This allows the user to access protected endpoints like /api/routing-policies
# which require the AdminPolicy authorization policy (= AIPolicy.Admin role).
# ---------------------------------------------------------------------------
echo ""
echo "=== Post-provisioning: Assigning AIPolicy.Admin role to deploying user ==="

if [ -z "${APP_ID:-}" ]; then
    echo "Skipping: api_app_id not set in azd env."
else
    SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)
    if [ -z "${SP_OBJECT_ID:-}" ]; then
        echo "Skipping: Could not resolve service principal for app $APP_ID."
    else
        ADMIN_ROLE_ID=$(az ad sp show --id "$APP_ID" --query "appRoles[?value=='AIPolicy.Admin'].id | [0]" -o tsv 2>/dev/null || true)
        if [ -z "${ADMIN_ROLE_ID:-}" ]; then
            echo "Skipping: Could not find AIPolicy.Admin app role on app $APP_ID."
        else
            USER_OBJECT_ID=$(az ad signed-in-user show --query "id" -o tsv 2>/dev/null || true)
            if [ -z "${USER_OBJECT_ID:-}" ]; then
                echo "Skipping: Could not resolve current user object ID."
            else
                EXISTING_COUNT=$(az rest --method GET \
                    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" \
                    --query "value[?principalId=='$USER_OBJECT_ID' && appRoleId=='$ADMIN_ROLE_ID'] | length(@)" \
                    -o tsv 2>/dev/null || echo "0")
                
                if [ "$EXISTING_COUNT" -gt 0 ]; then
                    echo "AIPolicy.Admin role already assigned — skipping."
                else
                    echo "Assigning AIPolicy.Admin role to user..."
                    BODY=$(python3 -c "import json; print(json.dumps({'principalId': '$USER_OBJECT_ID', 'resourceId': '$SP_OBJECT_ID', 'appRoleId': '$ADMIN_ROLE_ID'}))" 2>/dev/null || echo "")
                    if [ -z "$BODY" ]; then
                        echo "  ✗ python3 not available — cannot serialize request body. Install python3 or assign role manually."
                    else
                        TMP=$(mktemp)
                        echo "$BODY" > "$TMP"
                        if az rest --method POST \
                            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" \
                            --headers "Content-Type=application/json" \
                            --body "@$TMP" -o none 2>/dev/null; then
                            echo "  ✓ AIPolicy.Admin role assigned successfully."
                            echo "  ⚠ User must log out and log back in to receive a fresh token with the Admin role."
                        else
                            echo "  ✗ Failed to assign role (continuing)."
                        fi
                        rm -f "$TMP"
                    fi
                fi
            fi
        fi
    fi
fi

echo "=== Post-provisioning complete ==="
