#!/usr/bin/env bash
# Pre-provisioning script for Azure AI Policy Engine
# Called by azd before infrastructure provisioning.
# Ensures the API Entra ID app registration exists, persists its appId/tenantId
# into the azd environment (so main.parameters.json can pick them up), and writes
# src/aipolicyengine-ui/.env.production.local so the SPA build (run inside
# `dotnet publish`) bakes in the correct MSAL client/tenant IDs.

set -euo pipefail

echo "=== Pre-provisioning: Ensuring API Entra app registration ==="

get_azd_env() {
    azd env get-values 2>/dev/null | grep "^$1=" | sed "s/^$1=//" | tr -d '"' || true
}

APP_ID=$(get_azd_env CONTAINER_APP_CLIENT_ID)
TENANT_ID=$(get_azd_env ENTRA_ID_TENANT_ID)

if [ -n "${APP_ID:-}" ] && [ -n "${TENANT_ID:-}" ]; then
    echo "Reusing existing CONTAINER_APP_CLIENT_ID / ENTRA_ID_TENANT_ID from azd env."
    echo "  ClientId: $APP_ID"
    echo "  TenantId: $TENANT_ID"
else
    TENANT_ID=$(az account show --query tenantId -o tsv 2>/dev/null || true)
    if [ -z "${TENANT_ID:-}" ]; then
        echo "ERROR: Unable to read tenant ID from 'az account show'. Run 'az login' first." >&2
        exit 1
    fi

    ENV_NAME=$(get_azd_env AZURE_ENV_NAME)
    if [ -z "${ENV_NAME:-}" ]; then ENV_NAME="default"; fi
    DISPLAY_NAME="AI Policy Engine API ($ENV_NAME)"

    echo "Looking up Entra app '$DISPLAY_NAME'..."
    EXISTING_APP_ID=$(az ad app list --display-name "$DISPLAY_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)

    if [ -z "${EXISTING_APP_ID:-}" ]; then
        echo "Creating Entra app '$DISPLAY_NAME' (multi-tenant)..."
        APP_ID=$(az ad app create --display-name "$DISPLAY_NAME" --sign-in-audience AzureADMultipleOrgs --query "appId" -o tsv)
        if [ -z "${APP_ID:-}" ]; then
            echo "ERROR: Failed to create Entra app registration." >&2
            exit 1
        fi
        echo "  ✓ Created: $APP_ID"
    else
        APP_ID="$EXISTING_APP_ID"
        echo "  ✓ Reusing existing app: $APP_ID"
    fi

    azd env set CONTAINER_APP_CLIENT_ID "$APP_ID" >/dev/null
    azd env set ENTRA_ID_TENANT_ID "$TENANT_ID" >/dev/null

    echo "  ✓ azd env: CONTAINER_APP_CLIENT_ID=$APP_ID"
    echo "  ✓ azd env: ENTRA_ID_TENANT_ID=$TENANT_ID"
fi

# Ensure identifier URI, exposed scope, and SP are configured (run unconditionally
# so apps that pre-existed without these get fixed up).
echo "Ensuring identifier URI api://$APP_ID..."
az ad app update --id "$APP_ID" --identifier-uris "api://$APP_ID" -o none 2>/dev/null || \
    echo "WARNING: Failed to set identifier URI (continuing)."

echo "Ensuring API scope 'access_as_user' is exposed..."
API_OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)
EXISTING_SCOPE_ID=$(az ad app show --id "$APP_ID" --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>/dev/null || true)
if [ -z "${EXISTING_SCOPE_ID:-}" ]; then
    SCOPE_ID=$(python3 -c "import uuid; print(uuid.uuid4())" 2>/dev/null || uuidgen | tr 'A-Z' 'a-z')
    SCOPE_FILE=$(mktemp)
    cat > "$SCOPE_FILE" <<EOF
{"api":{"oauth2PermissionScopes":[{"id":"$SCOPE_ID","adminConsentDisplayName":"Access AI Policy Engine API","adminConsentDescription":"Allows the app to access the AI Policy Engine API on behalf of the signed-in user","userConsentDisplayName":"Access AI Policy Engine API","userConsentDescription":"Allows the app to access the AI Policy Engine API on your behalf","type":"User","value":"access_as_user","isEnabled":true}]}}
EOF
    if az rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$API_OBJECT_ID" \
        --headers "Content-Type=application/json" \
        --body "@$SCOPE_FILE" -o none 2>/dev/null; then
        echo "  ✓ Scope 'access_as_user' exposed (id: $SCOPE_ID)"
    else
        echo "WARNING: Failed to expose 'access_as_user' scope (continuing)."
    fi
    rm -f "$SCOPE_FILE"
else
    echo "  ✓ Scope 'access_as_user' already present"
fi

EXISTING_SP=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || true)
if [ -z "${EXISTING_SP:-}" ]; then
    echo "Creating service principal..."
    az ad sp create --id "$APP_ID" -o none 2>/dev/null || true
fi

# Ensure all three AIPolicy app roles are defined on the API app, then assign
# Admin + Export to the deploying user. Idempotent: only patches when a role
# is missing, only POSTs assignments that don't already exist.
echo "Ensuring AIPolicy app roles (Export/Admin/Apim) are defined..."
API_SP_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)
CURRENT_USER_OID=$(az ad signed-in-user show --query "id" -o tsv 2>/dev/null || true)

EXISTING_ROLES_JSON=$(az ad app show --id "$APP_ID" --query "appRoles" -o json 2>/dev/null || echo "[]")
ROLES_FILE=$(mktemp)
EXPORT_ROLE_ID=$(python3 -c "import json,sys,uuid; r=json.loads(sys.argv[1]); m={x['value']:x['id'] for x in r}; print(m.get('AIPolicy.Export',''))" "$EXISTING_ROLES_JSON")
ADMIN_ROLE_ID=$(python3 -c "import json,sys,uuid; r=json.loads(sys.argv[1]); m={x['value']:x['id'] for x in r}; print(m.get('AIPolicy.Admin',''))" "$EXISTING_ROLES_JSON")
APIM_ROLE_ID=$(python3 -c "import json,sys,uuid; r=json.loads(sys.argv[1]); m={x['value']:x['id'] for x in r}; print(m.get('AIPolicy.Apim',''))" "$EXISTING_ROLES_JSON")

NEED_PATCH=false
if [ -z "$EXPORT_ROLE_ID" ]; then EXPORT_ROLE_ID=$(python3 -c "import uuid; print(uuid.uuid4())"); NEED_PATCH=true; fi
if [ -z "$ADMIN_ROLE_ID" ];  then ADMIN_ROLE_ID=$(python3 -c "import uuid; print(uuid.uuid4())"); NEED_PATCH=true; fi
if [ -z "$APIM_ROLE_ID" ];   then APIM_ROLE_ID=$(python3 -c "import uuid; print(uuid.uuid4())"); NEED_PATCH=true; fi

if [ "$NEED_PATCH" = "true" ]; then
    python3 - "$EXISTING_ROLES_JSON" "$EXPORT_ROLE_ID" "$ADMIN_ROLE_ID" "$APIM_ROLE_ID" > "$ROLES_FILE" <<'PYEOF'
import json, sys
existing = json.loads(sys.argv[1])
exp_id, adm_id, apim_id = sys.argv[2], sys.argv[3], sys.argv[4]
defs = {
    "AIPolicy.Export": {"id": exp_id, "allowedMemberTypes": ["User", "Application"], "displayName": "AIPolicy Export",
                        "description": "Allows the user or application to export AIPolicy billing summaries and audit trails",
                        "value": "AIPolicy.Export", "isEnabled": True},
    "AIPolicy.Admin":  {"id": adm_id, "allowedMemberTypes": ["User", "Application"], "displayName": "AIPolicy Admin",
                        "description": "Allows the user or application to manage routing policies, plans, client assignments, and pricing",
                        "value": "AIPolicy.Admin", "isEnabled": True},
    "AIPolicy.Apim":   {"id": apim_id, "allowedMemberTypes": ["Application"], "displayName": "AIPolicy APIM Service",
                        "description": "Allows APIM to call the AIPolicy precheck and log ingest endpoints",
                        "value": "AIPolicy.Apim", "isEnabled": True},
}
by_value = {r["value"]: r for r in existing}
for v, d in defs.items():
    if v not in by_value:
        existing.append(d)
print(json.dumps({"appRoles": existing}))
PYEOF
    API_OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv 2>/dev/null)
    if az rest --method PATCH \
        --uri "https://graph.microsoft.com/v1.0/applications/$API_OBJECT_ID" \
        --headers "Content-Type=application/json" \
        --body "@$ROLES_FILE" -o none 2>/dev/null; then
        echo "  ✓ App roles defined"
    else
        echo "  ⚠ Failed to define app roles (continuing)"
    fi
else
    echo "  ✓ App roles AIPolicy.Export/Admin/Apim already defined"
fi
rm -f "$ROLES_FILE"

if [ -n "${API_SP_ID:-}" ] && [ -n "${CURRENT_USER_OID:-}" ]; then
    for ROLE_PAIR in "AIPolicy.Admin:$ADMIN_ROLE_ID" "AIPolicy.Export:$EXPORT_ROLE_ID"; do
        ROLE_NAME="${ROLE_PAIR%%:*}"
        ROLE_ID="${ROLE_PAIR##*:}"
        EXISTING_ASSIGN=$(az rest --method GET \
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$API_SP_ID/appRoleAssignedTo" \
            --query "value[?principalId=='$CURRENT_USER_OID' && appRoleId=='$ROLE_ID'] | [0].id" -o tsv 2>/dev/null || true)
        if [ -z "${EXISTING_ASSIGN:-}" ]; then
            ASSIGN_FILE=$(mktemp)
            cat > "$ASSIGN_FILE" <<EOF
{"principalId":"$CURRENT_USER_OID","resourceId":"$API_SP_ID","appRoleId":"$ROLE_ID"}
EOF
            if az rest --method POST \
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$API_SP_ID/appRoleAssignedTo" \
                --headers "Content-Type=application/json" --body "@$ASSIGN_FILE" -o none 2>/dev/null; then
                echo "  ✓ Assigned $ROLE_NAME to current user"
            else
                echo "  ⚠ Could not assign $ROLE_NAME (assign manually in Entra ID)"
            fi
            rm -f "$ASSIGN_FILE"
        else
            echo "  ✓ $ROLE_NAME already assigned to current user"
        fi
    done
else
    echo "  ⚠ Could not resolve API SP or signed-in user — skipping role assignment"
fi

# (Re)generate the SPA build-time env file so vite bakes in the correct IDs.
# This file is git-ignored and must always reflect the current azd-managed app,
# overwriting any stale values (e.g. from legacy setup-azure.ps1 runs).
# VITE_API_URL is set to empty string so the UI uses relative URLs (same-origin).
# This works because the UI is served from the same Container App as the API.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SPA_ENV_FILE="$SCRIPT_DIR/../src/aipolicyengine-ui/.env.production.local"
cat > "$SPA_ENV_FILE" <<EOF
# Auto-generated by scripts/preprovision.sh — do not edit by hand.
VITE_AZURE_CLIENT_ID=$APP_ID
VITE_AZURE_TENANT_ID=$TENANT_ID
VITE_AZURE_API_APP_ID=$APP_ID
VITE_AZURE_AUTHORITY=https://login.microsoftonline.com/$TENANT_ID
VITE_AZURE_SCOPE=api://$APP_ID/access_as_user
VITE_API_URL=
EOF
echo "  ✓ Wrote $SPA_ENV_FILE"

echo "=== Pre-provisioning complete ==="
