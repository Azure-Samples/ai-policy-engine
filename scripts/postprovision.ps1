#!/usr/bin/env pwsh
# Post-provisioning script for Azure AI Policy Engine
# Called by azd after infrastructure provisioning completes.
# Configures Cosmos DB data-plane RBAC (requires az CLI, not ARM).

$ErrorActionPreference = "Stop"

Write-Host "=== Post-provisioning: Configuring Cosmos DB data-plane RBAC ===" -ForegroundColor Cyan

# Read outputs from azd environment
$resourceGroup = azd env get-values | Select-String "^AZURE_RESOURCE_GROUP=" | ForEach-Object { $_.Line -replace "^AZURE_RESOURCE_GROUP=", "" } | ForEach-Object { $_.Trim('"') }
$cosmosEndpoint = azd env get-values | Select-String "^COSMOS_ENDPOINT=" | ForEach-Object { $_.Line -replace "^COSMOS_ENDPOINT=", "" } | ForEach-Object { $_.Trim('"') }

if ([string]::IsNullOrEmpty($resourceGroup)) {
    Write-Host "Skipping: AZURE_RESOURCE_GROUP not set" -ForegroundColor Yellow
    exit 0
}

# Get the Container App principal ID — list all then filter in PowerShell to avoid
# brittle JMESPath quote escaping when az.exe is invoked from pwsh on Windows.
$allContainerApps = az containerapp list --resource-group $resourceGroup -o json | ConvertFrom-Json
$containerApps = @($allContainerApps | Where-Object { $_.tags.'azd-service-name' -eq 'api' })
if ($containerApps.Count -eq 0) {
    Write-Host "Skipping: No container app found with azd-service-name=api tag" -ForegroundColor Yellow
    exit 0
}

$principalId = $containerApps[0].identity.principalId
$cosmosAccountName = $containerApps[0].properties.template.containers[0].env | Where-Object { $_.name -eq "ConnectionStrings__aipolicy" } | ForEach-Object { $_.value } | ForEach-Object { ([System.Uri]$_).Host.Split('.')[0] }

if ([string]::IsNullOrEmpty($cosmosAccountName)) {
    # Fallback: find Cosmos account in the resource group
    $cosmosAccounts = az cosmosdb list --resource-group $resourceGroup --query "[].name" -o json | ConvertFrom-Json
    if ($cosmosAccounts.Count -gt 0) {
        $cosmosAccountName = $cosmosAccounts[0]
    }
}

if ([string]::IsNullOrEmpty($cosmosAccountName) -or [string]::IsNullOrEmpty($principalId)) {
    Write-Host "Skipping Cosmos RBAC: could not resolve account name or principal ID" -ForegroundColor Yellow
    exit 0
}

Write-Host "Cosmos Account: $cosmosAccountName"
Write-Host "Principal ID:   $principalId"

# Built-in Cosmos DB Data Contributor role
$roleDefinitionId = "00000000-0000-0000-0000-000000000002"

# Check if role assignment already exists
$existing = az cosmosdb sql role assignment list `
    --account-name $cosmosAccountName `
    --resource-group $resourceGroup `
    --query "[?principalId=='$principalId' && roleDefinitionId contains '$roleDefinitionId']" `
    -o json 2>$null | ConvertFrom-Json

if ($existing.Count -gt 0) {
    Write-Host "Cosmos DB RBAC already configured — skipping." -ForegroundColor Green
} else {
    Write-Host "Assigning Cosmos DB Data Contributor role..."
    az cosmosdb sql role assignment create `
        --account-name $cosmosAccountName `
        --resource-group $resourceGroup `
        --role-definition-id $roleDefinitionId `
        --principal-id $principalId `
        --scope "/" `
        -o none
    Write-Host "Cosmos DB RBAC configured successfully." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Register the Container App FQDN as a SPA redirect URI on the API Entra app.
# Required so MSAL.js in the dashboard can complete the auth-code flow against
# https://<container-app-fqdn>. Without this, login fails with AADSTS50011.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Post-provisioning: Registering SPA redirect URI on API Entra app ===" -ForegroundColor Cyan

$appId = azd env get-values | Select-String "^CONTAINER_APP_CLIENT_ID=" | ForEach-Object { $_.Line -replace "^CONTAINER_APP_CLIENT_ID=", "" } | ForEach-Object { $_.Trim('"') }

if ([string]::IsNullOrEmpty($appId)) {
    Write-Host "Skipping: CONTAINER_APP_CLIENT_ID not set in azd env." -ForegroundColor Yellow
} elseif ($containerApps.Count -eq 0) {
    Write-Host "Skipping: container app not resolved earlier." -ForegroundColor Yellow
} else {
    $fqdn = $containerApps[0].properties.configuration.ingress.fqdn
    if ([string]::IsNullOrEmpty($fqdn)) {
        Write-Host "Skipping: container app has no ingress FQDN." -ForegroundColor Yellow
    } else {
        $redirectUri = "https://$fqdn"
        Write-Host "Container App FQDN: $fqdn"
        Write-Host "Redirect URI:       $redirectUri"

        # Look up the app's object id (Graph PATCH requires objectId, not appId).
        $objectId = az ad app show --id $appId --query "id" -o tsv 2>$null
        if ([string]::IsNullOrEmpty($objectId)) {
            Write-Host "ERROR: Could not resolve object id for app $appId." -ForegroundColor Red
        } else {
            $existingSpa = az rest --method GET `
                --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
                --query "spa" -o json 2>$null | ConvertFrom-Json

            $existingUris = @()
            if ($existingSpa -and $existingSpa.redirectUris) {
                $existingUris = @($existingSpa.redirectUris)
            }

            if ($existingUris -contains $redirectUri) {
                Write-Host "Redirect URI already registered — skipping." -ForegroundColor Green
            } else {
                $newUris = @($existingUris + $redirectUri | Select-Object -Unique)
                $bodyObj = @{ spa = @{ redirectUris = $newUris } }
                $bodyJson = $bodyObj | ConvertTo-Json -Depth 5 -Compress
                # az rest --body on Windows wants the JSON via a file or escaped string;
                # write to a temp file to avoid quote-escaping headaches.
                $tmp = New-TemporaryFile
                Set-Content -Path $tmp -Value $bodyJson -Encoding utf8
                az rest --method PATCH `
                    --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
                    --headers "Content-Type=application/json" `
                    --body "@$tmp" -o none
                Remove-Item $tmp -Force
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  ✓ Registered $redirectUri as SPA redirect URI." -ForegroundColor Green
                } else {
                    Write-Host "  ✗ Failed to register redirect URI (continuing)." -ForegroundColor Red
                }
            }
        }
    }
}

Write-Host "=== Post-provisioning complete ===" -ForegroundColor Cyan
