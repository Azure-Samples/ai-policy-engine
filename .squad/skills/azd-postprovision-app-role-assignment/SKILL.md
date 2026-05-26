# Skill: Entra App Role Assignment for Deploying User in azd Postprovision Hooks

**Category:** Infrastructure / DevOps / Azure  
**Applies to:** Projects using azd, Terraform, Entra ID (Azure AD) app registrations with app roles for authorization  
**Related:** ASP.NET Core authorization policies, MSAL.js authentication, role-based access control (RBAC)

## Problem

When deploying ASP.NET Core APIs to Azure that use Entra ID app roles for authorization:

1. API endpoints are protected with `[Authorize(Policy = "...")]` attributes that require specific app roles (e.g., `AIPolicy.Admin`)
2. Terraform can define app roles on the app registration and assign them to service principals, but **cannot assign roles to the deploying user** (user object ID is unknown at Terraform apply time)
3. Users who deploy via `azd up` can successfully authenticate (log in), but receive **HTTP 403 Forbidden** on endpoints requiring app roles because their token has no roles
4. Manual role assignment via Azure Portal or Graph API is a poor first-run experience

**403 vs 401:**
- **HTTP 401 Unauthorized** = authentication failed (no token, expired token, wrong audience, missing redirect URI)
- **HTTP 403 Forbidden** = authenticated BUT not authorized (token is valid but missing required scope/role/claim)

## Solution Pattern

Use azd postprovision hooks to:

1. Query the current signed-in user's object ID (the person who ran `azd up`)
2. Identify the API app registration (from Terraform outputs in azd env)
3. Query the target app role ID (e.g., `AIPolicy.Admin`) from the app registration
4. Assign the user to the app role using Microsoft Graph API (`appRoleAssignedTo`)
5. Make the operation idempotent (check if user already has the role before assigning)
6. Warn the user that they must log out and log back in to receive a fresh token with the role

## Implementation

### Postprovision Script Pattern (PowerShell)

```powershell
# Get the API app ID from azd environment (Terraform output)
$appId = azd env get-values | Select-String "^api_app_id=" | ForEach-Object { $_.Line -replace "^api_app_id=", "" } | ForEach-Object { $_.Trim('"') }

# Get the service principal object ID
$spObjectId = az ad sp show --id $appId --query "id" -o tsv 2>$null

# Get the target app role ID (e.g., AIPolicy.Admin)
$adminRoleId = az ad sp show --id $appId --query "appRoles[?value=='AIPolicy.Admin'].id | [0]" -o tsv 2>$null

# Get the current user's object ID
$userObjectId = az ad signed-in-user show --query "id" -o tsv 2>$null

# Check if user already has the role (idempotent check)
$existingAssignments = az rest --method GET `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignedTo" `
    --query "value[?principalId=='$userObjectId' && appRoleId=='$adminRoleId']" `
    -o json 2>$null | ConvertFrom-Json

if ($existingAssignments.Count -gt 0) {
    Write-Host "AIPolicy.Admin role already assigned — skipping." -ForegroundColor Green
} else {
    # Assign the role
    $bodyObj = @{
        principalId = $userObjectId
        resourceId  = $spObjectId
        appRoleId   = $adminRoleId
    }
    $bodyJson = $bodyObj | ConvertTo-Json -Compress
    $tmp = New-TemporaryFile
    Set-Content -Path $tmp -Value $bodyJson -Encoding utf8
    az rest --method POST `
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignedTo" `
        --headers "Content-Type=application/json" `
        --body "@$tmp" -o none 2>$null
    Remove-Item $tmp -Force
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ AIPolicy.Admin role assigned successfully." -ForegroundColor Green
        Write-Host "  ⚠ User must log out and log back in to receive a fresh token with the Admin role." -ForegroundColor Yellow
    }
}
```

### Postprovision Script Pattern (Bash)

```bash
# Get the API app ID from azd environment (Terraform output)
APP_ID=$(azd env get-values 2>/dev/null | grep "^api_app_id=" | sed "s/^api_app_id=//" | tr -d '"' || true)

# Get the service principal object ID
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)

# Get the target app role ID (e.g., AIPolicy.Admin)
ADMIN_ROLE_ID=$(az ad sp show --id "$APP_ID" --query "appRoles[?value=='AIPolicy.Admin'].id | [0]" -o tsv 2>/dev/null || true)

# Get the current user's object ID
USER_OBJECT_ID=$(az ad signed-in-user show --query "id" -o tsv 2>/dev/null || true)

# Check if user already has the role (idempotent check)
EXISTING_COUNT=$(az rest --method GET \
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" \
    --query "value[?principalId=='$USER_OBJECT_ID' && appRoleId=='$ADMIN_ROLE_ID'] | length(@)" \
    -o tsv 2>/dev/null || echo "0")

if [ "$EXISTING_COUNT" -gt 0 ]; then
    echo "AIPolicy.Admin role already assigned — skipping."
else
    # Assign the role
    BODY=$(python3 -c "import json; print(json.dumps({'principalId': '$USER_OBJECT_ID', 'resourceId': '$SP_OBJECT_ID', 'appRoleId': '$ADMIN_ROLE_ID'}))" 2>/dev/null || echo "")
    TMP=$(mktemp)
    echo "$BODY" > "$TMP"
    if az rest --method POST \
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" \
        --headers "Content-Type=application/json" \
        --body "@$TMP" -o none 2>/dev/null; then
        echo "  ✓ AIPolicy.Admin role assigned successfully."
        echo "  ⚠ User must log out and log back in to receive a fresh token with the Admin role."
    fi
    rm -f "$TMP"
fi
```

### ASP.NET Core Authorization Policy (C#)

```csharp
// Program.cs or Startup.cs
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminPolicy", policy =>
        policy.RequireRole("AIPolicy.Admin"))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// Endpoint registration
routes.MapGet("/api/routing-policies", ListPolicies)
    .RequireAuthorization("AdminPolicy");
```

### Terraform App Role Definition

```hcl
# infra/terraform/modules/identity/main.tf

resource "random_uuid" "role_admin" {}

resource "azuread_application" "api" {
  display_name     = "AI Policy API"
  sign_in_audience = "AzureADMyOrg"

  app_role {
    id                   = random_uuid.role_admin.result
    display_name         = "AI Policy Admin"
    description          = "Full administrative access to the AI Policy system"
    value                = "AIPolicy.Admin"
    allowed_member_types = ["Application", "User"]
    enabled              = true
  }
}
```

## Verification

After running the postprovision hook:

```powershell
# Get the app ID
$APP_ID = azd env get-values | Select-String "^api_app_id=" | ForEach-Object { $_.Line -replace "^api_app_id=", "" } | ForEach-Object { $_.Trim('"') }

# Get the service principal object ID
$SP_OBJECT_ID = az ad sp show --id $APP_ID --query "id" -o tsv

# List all app role assignments (users and service principals with roles)
az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignedTo" --query "value[].{principal:principalDisplayName,role:appRoleId}" -o table
```

## Token Refresh Requirement

**CRITICAL:** App role assignments do NOT affect existing tokens. Tokens are issued by Entra ID at login time and cached by MSAL for ~1 hour (default).

Users must obtain a fresh token after role assignment:
- **Option 1:** Log out and log back in (MSAL clears token cache on logout)
- **Option 2:** Wait for token expiry (~1 hour by default)
- **Option 3:** Clear browser storage manually (MSAL stores tokens in localStorage)

MSAL token cache keys:
- `msal.<client-id>.token.keys`
- `msal.<client-id>.idtoken`
- `msal.<client-id>.accesstoken`

## Common Issues

### HTTP 403 after successful login

**Cause:** User is authenticated (token is valid) but their token has no app roles (role assignment missing or token not refreshed).

**Symptoms:**
- Login succeeds (redirect URI works, no AADSTS errors)
- API returns HTTP 403 on endpoints requiring app roles
- Browser developer tools show no `roles` claim in the ID token or access token

**Fix:**
1. Check if the user has the role assigned:
   ```powershell
   az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<sp-object-id>/appRoleAssignedTo" --query "value[?principalId=='<user-object-id>']"
   ```
2. If missing, run `azd hooks run postprovision` to assign the role
3. **Log out and log back in** to receive a fresh token

### Postprovision silently skips app role assignment

**Cause:** Script queries the wrong environment variable name (e.g., `AZURE_RESOURCE_GROUP` instead of `resource_group_name`). See the redirect URI skill for details.

**Fix:** Ensure postprovision script uses Terraform's exact output variable names (snake_case).

### "Insufficient privileges to complete the operation"

**Cause:** The deploying user's Entra ID account lacks permission to assign app roles (requires `AppRoleAssignment.ReadWrite.All` or `RoleManagement.ReadWrite.Directory` Graph API permissions).

**Common scenarios:**
- Guest users in a tenant (external accounts)
- Non-admin users in locked-down tenants

**Workarounds:**
1. Request a tenant admin to run `azd hooks run postprovision`
2. Request a tenant admin to assign the role manually via Azure Portal:
   - Go to Enterprise Applications → AI Policy API → Users and groups → Add user/group
   - Select the user and assign the "AI Policy Admin" role
3. Use a service principal with sufficient permissions for `azd up` (advanced)

### Only the deploying user has admin access

**Cause:** Postprovision only assigns the role to the person who ran `azd up`.

**Fix (per-user onboarding):** Each team member can run:
```bash
azd hooks run postprovision
```
This assigns the role to their account (idempotent — safe to re-run).

**Fix (bulk assignment):** A tenant admin can assign roles to a security group via Azure Portal:
- Go to Enterprise Applications → AI Policy API → Users and groups → Add user/group
- Select a security group (e.g., "AI Policy Admins")
- Assign the "AI Policy Admin" role
- Add users to the security group (users inherit the role)

## Why Postprovision (Not Terraform)

- **Terraform requires user object ID at apply time** — not available for the deploying user (dynamic at runtime)
- **Postprovision has access to the deploying user** via `az ad signed-in-user show` (uses the already-authenticated az CLI context)
- **Consistent with existing patterns** — postprovision already handles redirect URI registration (another runtime-dependent value)

## App Roles vs Scopes (Key Differences)

| Concept | App Roles | Scopes (Delegated Permissions) |
|---------|-----------|--------------------------------|
| **Purpose** | Assign roles to users/apps | Grant delegated permissions to call APIs |
| **Claim in token** | `roles` array | `scp` string (space-separated) |
| **Defined on** | Resource app (API app) | Resource app (API app) |
| **Assigned to** | Users, Groups, Service Principals | Client app (via `required_resource_access`) |
| **Consent** | Admin assigns role | User/admin grants consent |
| **ASP.NET Core** | `.RequireRole("AIPolicy.Admin")` | `.RequireScope("access_as_user")` |
| **OAuth2/OIDC** | Not part of OAuth2 spec (Microsoft extension) | Standard OAuth2 `scope` parameter |
| **Use case** | Authorization (what can this user DO) | Authentication (what APIs can this app call) |

**Rule of thumb:** Use **app roles** for coarse-grained authorization (Admin, User, Viewer). Use **scopes** for API-to-API calls (delegated access).

## References

- **Graph API:** `POST /servicePrincipals/{id}/appRoleAssignedTo` (assigns user/app to app role)
- **Graph API:** `GET /servicePrincipals/{id}/appRoleAssignedTo` (list all role assignments)
- **Microsoft Docs:** [App roles in Azure AD](https://learn.microsoft.com/en-us/azure/active-directory/develop/howto-add-app-roles-in-azure-ad-apps)
- **Microsoft Docs:** [Role-based authorization in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/roles)

## Tags

#azure #entra-id #app-roles #authorization #rbac #http-403 #msal #azd #postprovision #graph-api #terraform #asp-net-core
