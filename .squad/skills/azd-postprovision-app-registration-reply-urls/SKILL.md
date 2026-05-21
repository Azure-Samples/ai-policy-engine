# Skill: Azure AD App Registration Redirect URI Setup in azd Postprovision Hooks

**Category:** Infrastructure / DevOps / Azure  
**Applies to:** Projects using azd, Terraform, and Entra ID (Azure AD) app registrations with SPA/web redirect URIs  
**Related:** MSAL.js authentication, Container Apps, SPA deployment

## Problem

When deploying SPAs (Single Page Applications) to Azure that use MSAL.js for authentication:

1. The deployed URL isn't known until AFTER infrastructure is provisioned
2. Entra ID app registrations require redirect URIs to be registered BEFORE the app can complete authentication flows
3. Users get AADSTS500113 ("No reply address is registered for the application") if redirect URIs aren't set correctly
4. Multiple app registrations may exist (Terraform-managed, legacy, manual), causing confusion about which one to configure

## Solution Pattern

Use azd postprovision hooks to:

1. Query the ACTUAL deployed URL from Azure (not from Terraform state, which can be stale)
2. Identify the CORRECT app registration (the one the SPA is configured to use)
3. Register the deployed URL as a redirect URI using Microsoft Graph API
4. Make the operation idempotent (safe to re-run without duplicating URIs)

## Implementation

See `.squad/decisions/inbox/sydnor-redirect-uri-postprovision-pattern.md` for full implementation details.

Key script locations:
- `scripts/postprovision.ps1` (Windows)
- `scripts/postprovision.sh` (Linux/macOS)

## Verification

After running the postprovision hook:

```bash
# Get the app ID
APP_ID=$(azd env get-values | grep api_app_id | cut -d= -f2 | tr -d '"')

# Check registered redirect URIs
az ad app show --id "$APP_ID" --query "{displayName:displayName,spa:spa.redirectUris}" -o json
```

## Common Issues

### AADSTS500113: No reply address is registered for the application

**Cause:** The redirect URI is missing or doesn't match the URL the SPA is served from.

**Fix:** Run `azd hooks run postprovision` to register the redirect URI.

### Redirect URI registered on the wrong app

**Cause:** Multiple app registrations exist. The postprovision script is using the wrong `APP_ID`.

**Fix:** Check which app the SPA is configured to use (e.g., `VITE_AZURE_CLIENT_ID` in .env files) and ensure the postprovision script uses the matching Terraform output variable.

### Postprovision silently skips redirect URI registration

**Cause (fresh deploy):** Postprovision script queries the wrong environment variable name. Terraform outputs use snake_case (e.g., `resource_group_name`), but the script may query SCREAMING_CASE azd built-ins (e.g., `AZURE_RESOURCE_GROUP`).

**Symptom:** `azd hooks run postprovision` prints "Skipping: AZURE_RESOURCE_GROUP not set" or similar, even though the resource group exists.

**Fix:** Update postprovision script to use Terraform's exact output variable names:
```powershell
# WRONG (queries azd built-in, not Terraform output)
$resourceGroup = azd env get-values | Select-String "^AZURE_RESOURCE_GROUP="

# CORRECT (queries Terraform output)
$resourceGroup = azd env get-values | Select-String "^resource_group_name="
```

**Why this matters:** On fresh deploys, only Terraform's output names are guaranteed to be in the azd environment. On update deploys, both may exist from prior manual runs, masking the bug.

## Tags

#azure #entra-id #app-registration #redirect-uri #msal #spa #azd #postprovision #graph-api #container-apps
