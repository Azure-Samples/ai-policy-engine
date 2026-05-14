#!/usr/bin/env pwsh
# Pre-provisioning script for Azure AI Policy Engine
# Called by azd before infrastructure provisioning.
# Ensures the API Entra ID app registration exists, persists its appId/tenantId
# into the azd environment (so main.parameters.json can pick them up), and writes
# src/aipolicyengine-ui/.env.production.local so the SPA build (run inside
# `dotnet publish`) bakes in the correct MSAL client/tenant IDs.

$ErrorActionPreference = "Stop"

Write-Host "=== Pre-provisioning: Ensuring API Entra app registration ===" -ForegroundColor Cyan

function Get-AzdEnvValue {
    param([string]$Name)
    $line = azd env get-values | Select-String "^$Name="
    if (-not $line) { return "" }
    return ($line.Line -replace "^$Name=", "").Trim('"')
}

# 1. Resolve / create the API Entra app and capture appId + tenantId.
$appId = Get-AzdEnvValue -Name "CONTAINER_APP_CLIENT_ID"
$tenantId = Get-AzdEnvValue -Name "ENTRA_ID_TENANT_ID"

if (-not [string]::IsNullOrEmpty($appId) -and -not [string]::IsNullOrEmpty($tenantId)) {
    Write-Host "Reusing existing CONTAINER_APP_CLIENT_ID / ENTRA_ID_TENANT_ID from azd env." -ForegroundColor Green
    Write-Host "  ClientId: $appId" -ForegroundColor Gray
    Write-Host "  TenantId: $tenantId" -ForegroundColor Gray
} else {
    # Resolve tenant from the active az login.
    $tenantId = (az account show --query tenantId -o tsv 2>$null)
    if ([string]::IsNullOrEmpty($tenantId)) {
        Write-Host "ERROR: Unable to read tenant ID from 'az account show'. Run 'az login' first." -ForegroundColor Red
        exit 1
    }

    # Per-environment app display name so multiple azd environments don't collide.
    $envName = Get-AzdEnvValue -Name "AZURE_ENV_NAME"
    if ([string]::IsNullOrEmpty($envName)) { $envName = "default" }
    $displayName = "AI Policy Engine API ($envName)"

    Write-Host "Looking up Entra app '$displayName'..." -ForegroundColor Gray
    $existingAppId = az ad app list --display-name $displayName --query "[0].appId" -o tsv 2>$null

    if ([string]::IsNullOrEmpty($existingAppId)) {
        Write-Host "Creating Entra app '$displayName' (multi-tenant)..." -ForegroundColor Gray
        $appId = az ad app create --display-name $displayName --sign-in-audience AzureADMultipleOrgs --query "appId" -o tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($appId)) {
            Write-Host "ERROR: Failed to create Entra app registration." -ForegroundColor Red
            exit 1
        }
        Write-Host "  ✓ Created: $appId" -ForegroundColor Green
    } else {
        $appId = $existingAppId
        Write-Host "  ✓ Reusing existing app: $appId" -ForegroundColor Green
    }

    # Persist into azd environment so main.parameters.json can substitute them.
    azd env set CONTAINER_APP_CLIENT_ID $appId | Out-Null
    azd env set ENTRA_ID_TENANT_ID $tenantId | Out-Null

    Write-Host "  ✓ azd env: CONTAINER_APP_CLIENT_ID=$appId" -ForegroundColor Green
    Write-Host "  ✓ azd env: ENTRA_ID_TENANT_ID=$tenantId" -ForegroundColor Green
}

# 2. Ensure Application ID URI, exposed scope, and service principal are configured.
#    Run unconditionally (even when reusing values from azd env) so that an app whose
#    identifier-uri or 'access_as_user' scope was never set up still gets fixed.
Write-Host "Ensuring identifier URI api://$appId..." -ForegroundColor Gray
az ad app update --id $appId --identifier-uris "api://$appId" -o none 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: Failed to set identifier URI (continuing)." -ForegroundColor Yellow
}

Write-Host "Ensuring API scope 'access_as_user' is exposed..." -ForegroundColor Gray
$apiObjectId = az ad app show --id $appId --query "id" -o tsv 2>$null
$existingScopeId = az ad app show --id $appId --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($existingScopeId)) {
    $scopeId = [guid]::NewGuid().ToString()
    $scopeBody = @{
        api = @{
            oauth2PermissionScopes = @(@{
                id                      = $scopeId
                adminConsentDisplayName = "Access AI Policy Engine API"
                adminConsentDescription = "Allows the app to access the AI Policy Engine API on behalf of the signed-in user"
                userConsentDisplayName  = "Access AI Policy Engine API"
                userConsentDescription  = "Allows the app to access the AI Policy Engine API on your behalf"
                type                    = "User"
                value                   = "access_as_user"
                isEnabled               = $true
            })
        }
    } | ConvertTo-Json -Depth 5 -Compress
    $scopeFile = New-TemporaryFile
    [System.IO.File]::WriteAllText($scopeFile, $scopeBody, [System.Text.UTF8Encoding]::new($false))
    az rest --method PATCH `
        --uri "https://graph.microsoft.com/v1.0/applications/$apiObjectId" `
        --headers "Content-Type=application/json" `
        --body "@$scopeFile" -o none
    Remove-Item $scopeFile -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: Failed to expose 'access_as_user' scope (continuing)." -ForegroundColor Yellow
    } else {
        Write-Host "  ✓ Scope 'access_as_user' exposed (id: $scopeId)" -ForegroundColor Green
    }
} else {
    Write-Host "  ✓ Scope 'access_as_user' already present" -ForegroundColor Green
}

# Ensure a service principal exists in this tenant for the app.
$existingSp = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv 2>$null
if ([string]::IsNullOrEmpty($existingSp)) {
    Write-Host "Creating service principal..." -ForegroundColor Gray
    az ad sp create --id $appId -o none 2>$null
}

# Ensure all three AIPolicy app roles are defined on the API app, then assign
# Admin + Export to the deploying user. Idempotent: only patches when a role
# is missing, only POSTs assignments that don't already exist.
Write-Host "Ensuring AIPolicy app roles (Export/Admin/Apim) are defined..." -ForegroundColor Gray
$apiSpId = az ad sp show --id $appId --query "id" -o tsv 2>$null
$currentUserOid = az ad signed-in-user show --query "id" -o tsv 2>$null

$existingRolesJson = az ad app show --id $appId --query "appRoles" -o json 2>$null
$existingRoles = [System.Collections.ArrayList]::new()
if (-not [string]::IsNullOrWhiteSpace($existingRolesJson)) {
    foreach ($r in @($existingRolesJson | ConvertFrom-Json)) { [void]$existingRoles.Add($r) }
}
$rolesByValue = @{}
foreach ($r in $existingRoles) { $rolesByValue[$r.value] = $r }

$roleSpecs = @(
    @{ Value = "AIPolicy.Export"; DisplayName = "AIPolicy Export"; Description = "Allows the user or application to export AIPolicy billing summaries and audit trails"; AllowedMemberTypes = @("User", "Application") },
    @{ Value = "AIPolicy.Admin";  DisplayName = "AIPolicy Admin";  Description = "Allows the user or application to manage routing policies, plans, client assignments, and pricing"; AllowedMemberTypes = @("User", "Application") },
    @{ Value = "AIPolicy.Apim";   DisplayName = "AIPolicy APIM Service"; Description = "Allows APIM to call the AIPolicy precheck and log ingest endpoints"; AllowedMemberTypes = @("Application") }
)

$rolesChanged = $false
foreach ($spec in $roleSpecs) {
    if (-not $rolesByValue.ContainsKey($spec.Value)) {
        $newRole = [PSCustomObject]@{
            id                 = [guid]::NewGuid().ToString()
            allowedMemberTypes = $spec.AllowedMemberTypes
            displayName        = $spec.DisplayName
            description        = $spec.Description
            value              = $spec.Value
            isEnabled          = $true
        }
        [void]$existingRoles.Add($newRole)
        $rolesByValue[$spec.Value] = $newRole
        $rolesChanged = $true
    }
}

if ($rolesChanged) {
    $rolesBody = @{ appRoles = @($existingRoles) } | ConvertTo-Json -Depth 5 -Compress
    $rolesFile = New-TemporaryFile
    [System.IO.File]::WriteAllText($rolesFile, $rolesBody, [System.Text.UTF8Encoding]::new($false))
    az rest --method PATCH `
        --uri "https://graph.microsoft.com/v1.0/applications/$apiObjectId" `
        --headers "Content-Type=application/json" `
        --body "@$rolesFile" -o none
    Remove-Item $rolesFile -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ⚠ Failed to define app roles (continuing)" -ForegroundColor Yellow
    } else {
        Write-Host "  ✓ App roles defined" -ForegroundColor Green
    }
} else {
    Write-Host "  ✓ App roles AIPolicy.Export/Admin/Apim already defined" -ForegroundColor Green
}

if (-not [string]::IsNullOrWhiteSpace($apiSpId) -and -not [string]::IsNullOrWhiteSpace($currentUserOid)) {
    foreach ($roleValue in @("AIPolicy.Admin", "AIPolicy.Export")) {
        $roleId = $rolesByValue[$roleValue].id
        $existingAssign = az rest --method GET `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
            --query "value[?principalId=='$currentUserOid' && appRoleId=='$roleId'] | [0].id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingAssign)) {
            $assignBody = @{
                principalId = $currentUserOid
                resourceId  = $apiSpId
                appRoleId   = $roleId
            } | ConvertTo-Json -Compress
            $assignFile = New-TemporaryFile
            [System.IO.File]::WriteAllText($assignFile, $assignBody, [System.Text.UTF8Encoding]::new($false))
            az rest --method POST `
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null
            Remove-Item $assignFile -Force -ErrorAction SilentlyContinue
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Assigned $roleValue to current user" -ForegroundColor Green
            } else {
                Write-Host "  ⚠ Could not assign $roleValue (assign manually in Entra ID)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "  ✓ $roleValue already assigned to current user" -ForegroundColor Green
        }
    }
} else {
    Write-Host "  ⚠ Could not resolve API SP or signed-in user — skipping role assignment" -ForegroundColor Yellow
}

# 3. (Re)generate the SPA build-time env file so vite bakes in the correct IDs.
#    This file is git-ignored and must always reflect the current azd-managed app,
#    overwriting any stale values (e.g. from legacy setup-azure.ps1 runs).
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$spaEnvFile = Join-Path $repoRoot "src/aipolicyengine-ui/.env.production.local"
$spaEnvContent = @"
# Auto-generated by scripts/preprovision.ps1 — do not edit by hand.
VITE_AZURE_CLIENT_ID=$appId
VITE_AZURE_TENANT_ID=$tenantId
VITE_AZURE_API_APP_ID=$appId
VITE_AZURE_AUTHORITY=https://login.microsoftonline.com/$tenantId
VITE_AZURE_SCOPE=api://$appId/access_as_user
"@
Set-Content -Path $spaEnvFile -Value $spaEnvContent -Encoding utf8
Write-Host "  ✓ Wrote $spaEnvFile" -ForegroundColor Green

Write-Host "=== Pre-provisioning complete ===" -ForegroundColor Cyan
