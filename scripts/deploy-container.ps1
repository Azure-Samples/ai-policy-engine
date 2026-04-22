<#
.SYNOPSIS
    Builds the AI Policy API container image and deploys it to the provisioned infrastructure.
.DESCRIPTION
    Post-deploy script to run after Terraform (or Bicep) has provisioned the infrastructure.
    Builds the Docker image (multi-stage: Node.js UI + .NET API), pushes to ACR,
    and updates the Container App to use the new image.

    This two-stage approach is required because:
    1. Terraform deploys infrastructure with a placeholder image (mcr.microsoft.com/dotnet/aspnet:10.0)
    2. This script builds the real image, pushes to the deployed ACR, and updates the Container App
    
    This is the standard pattern for enterprise environments where public container registries are disabled.
.PARAMETER ResourceGroupName
    Resource group containing the deployed infrastructure.
.PARAMETER WorkloadName
    Workload name prefix used during infrastructure deployment (default: aipolicy).
.PARAMETER Tag
    Image tag (default: timestamped run-YYYYMMDDHHMMSS).
.PARAMETER SkipBuild
    Skip the Docker build step (push and update only — assumes image is already built locally).
.EXAMPLE
    .\deploy-container.ps1 -ResourceGroupName rg-aipolicy-eastus2
.EXAMPLE
    .\deploy-container.ps1 -ResourceGroupName rg-aipolicy-eastus2 -WorkloadName aipolicy -Tag v1.0.0
#>
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroupName,
    [string]$WorkloadName = "aipolicy",
    [string]$Tag = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  AI Policy API — Container Build & Deploy                ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Discover infrastructure ──────────────────────────────────

Write-Host "  Step 1: Discovering infrastructure..." -ForegroundColor Yellow

$acrName = az acr list --resource-group $ResourceGroupName --query "[0].name" -o tsv
if ([string]::IsNullOrWhiteSpace($acrName)) { throw "No ACR found in resource group $ResourceGroupName" }
Write-Host "    ACR: $acrName" -ForegroundColor Green

$containerAppName = az containerapp list --resource-group $ResourceGroupName --query "[0].name" -o tsv
if ([string]::IsNullOrWhiteSpace($containerAppName)) { throw "No Container App found in resource group $ResourceGroupName" }
Write-Host "    Container App: $containerAppName" -ForegroundColor Green

$acrLoginServer = az acr show --name $acrName --query "loginServer" -o tsv
Write-Host "    ACR Login Server: $acrLoginServer" -ForegroundColor Green

# Resolve infrastructure details — prefer Terraform outputs when available,
# fall back to Azure CLI queries for standalone usage.
$tfDir = Join-Path $RepoRoot "infra\terraform"
$tenantId = $null; $apiAppId = $null; $containerAppFqdn = $null
if (Test-Path (Join-Path $tfDir "terraform.tfstate")) {
    Push-Location $tfDir
    try {
        $tenantId = (terraform output -raw tenant_id 2>$null)
        $apiAppId = (terraform output -raw api_app_id 2>$null)
        $containerAppFqdn = (terraform output -raw container_app_url 2>$null) -replace '^https://',''
    } catch {}
    Pop-Location
}
if ([string]::IsNullOrWhiteSpace($tenantId)) { $tenantId = az account show --query "tenantId" -o tsv }
if ([string]::IsNullOrWhiteSpace($containerAppFqdn)) {
    $containerAppFqdn = az containerapp show --name $containerAppName --resource-group $ResourceGroupName --query "properties.configuration.ingress.fqdn" -o tsv
}
if ([string]::IsNullOrWhiteSpace($apiAppId)) {
    $apiAppId = az ad app list --display-name "AI Policy API" --query "[0].appId" -o tsv
}
Write-Host "    Tenant: $tenantId" -ForegroundColor Green
Write-Host "    API App: $apiAppId" -ForegroundColor Green
Write-Host "    Container App FQDN: $containerAppFqdn" -ForegroundColor Green

# ── Step 1b: Ensure Entra app config is correct ──────────────────────
# The AzureAD Terraform provider has eventual-consistency issues where
# identifier URIs and redirect URIs silently fail. This step guarantees
# all Entra app config is correct before we build the container image
# (which bakes the UI env file into the image).

Write-Host ""
Write-Host "  Step 1b: Verifying Entra ID app configuration..." -ForegroundColor Yellow

$graphToken = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv

# ── API App: identifier URI ──
$currentUris = @(az ad app show --id $apiAppId --query "identifierUris[]" -o tsv)
if ($currentUris -notcontains "api://$apiAppId") {
    Write-Host "    ⚠ API identifier URI missing — adding api://$apiAppId" -ForegroundColor DarkYellow
    az ad app update --id $apiAppId --identifier-uris "api://$apiAppId"
} else {
    Write-Host "    ✓ API identifier URI: api://$apiAppId" -ForegroundColor Green
}

# ── API App: SPA redirect URIs ──
$currentSpa = @(az ad app show --id $apiAppId --query "spa.redirectUris[]" -o tsv)
$requiredUris = @("https://$containerAppFqdn", "http://localhost:5173")
$missing = @($requiredUris | Where-Object { $currentSpa -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Host "    ⚠ SPA redirect URIs missing — setting..." -ForegroundColor DarkYellow
    $allUris = @(@($currentSpa) + @($requiredUris) | Where-Object { $_ } | Select-Object -Unique)
    $objId = az ad app show --id $apiAppId --query "id" -o tsv
    $body = @{spa=@{redirectUris=$allUris}} | ConvertTo-Json -Depth 5 -Compress
    Invoke-RestMethod -Method Patch -Uri "https://graph.microsoft.com/v1.0/applications/$objId" -Headers @{Authorization="Bearer $graphToken"; 'Content-Type'='application/json'} -Body $body
    Write-Host "    ✓ SPA redirect URIs configured" -ForegroundColor Green
} else {
    Write-Host "    ✓ SPA redirect URIs OK" -ForegroundColor Green
}

# ── Gateway App: identifier URI ──
$gatewayAppId = $null
if (Test-Path (Join-Path $tfDir "terraform.tfstate")) {
    Push-Location $tfDir
    try { $gatewayAppId = (terraform output -raw gateway_app_id 2>$null) } catch {}
    Pop-Location
}
if ([string]::IsNullOrWhiteSpace($gatewayAppId)) {
    # Fallback: look up by display name (Bicep-path deployments)
    $gwLookup = az ad app list --display-name "AI Policy APIM Gateway" --query "[0].appId" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($gwLookup)) { $gatewayAppId = $gwLookup }
}
if (-not [string]::IsNullOrWhiteSpace($gatewayAppId)) {
    $gwUris = @(az ad app show --id $gatewayAppId --query "identifierUris[]" -o tsv)
    if ($gwUris -notcontains "api://$gatewayAppId") {
        Write-Host "    ⚠ Gateway identifier URI missing — adding api://$gatewayAppId" -ForegroundColor DarkYellow
        az ad app update --id $gatewayAppId --identifier-uris "api://$gatewayAppId"
    } else {
        Write-Host "    ✓ Gateway identifier URI: api://$gatewayAppId" -ForegroundColor Green
    }
} else {
    Write-Host "    ⊘ Gateway app ID not found — skipping" -ForegroundColor DarkGray
}

# ── API App: service principal exists ──
$apiSpId = az ad sp list --filter "appId eq '$apiAppId'" --query "[0].id" -o tsv
if ([string]::IsNullOrWhiteSpace($apiSpId)) {
    Write-Host "    ⚠ API service principal missing — creating..." -ForegroundColor DarkYellow
    az ad sp create --id $apiAppId -o none
    $apiSpId = az ad sp list --filter "appId eq '$apiAppId'" --query "[0].id" -o tsv
    Write-Host "    ✓ API service principal created" -ForegroundColor Green
} else {
    Write-Host "    ✓ API service principal exists" -ForegroundColor Green
}

# ── Gateway App: service principal exists ──
if (-not [string]::IsNullOrWhiteSpace($gatewayAppId)) {
    $gwSpId = az ad sp list --filter "appId eq '$gatewayAppId'" --query "[0].id" -o tsv
    if ([string]::IsNullOrWhiteSpace($gwSpId)) {
        Write-Host "    ⚠ Gateway service principal missing — creating..." -ForegroundColor DarkYellow
        az ad sp create --id $gatewayAppId -o none
        Write-Host "    ✓ Gateway service principal created" -ForegroundColor Green
    } else {
        Write-Host "    ✓ Gateway service principal exists" -ForegroundColor Green
    }
}

# ── Deploying user: ensure Admin and Export roles ──
Write-Host ""
Write-Host "  Step 1c: Ensuring deployer has Admin and Export roles..." -ForegroundColor Yellow

$currentUserId = az ad signed-in-user show --query "id" -o tsv
$appRoles = az ad app show --id $apiAppId --query "appRoles[].{id:id,value:value}" -o json | ConvertFrom-Json
$adminRoleId = ($appRoles | Where-Object { $_.value -eq 'AIPolicy.Admin' }).id
$exportRoleId = ($appRoles | Where-Object { $_.value -eq 'AIPolicy.Export' }).id

$existingAssignments = az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" 2>$null | ConvertFrom-Json
$userAssignments = @($existingAssignments.value | Where-Object { $_.principalId -eq $currentUserId })

foreach ($role in @(@{id=$adminRoleId; name='AIPolicy.Admin'}, @{id=$exportRoleId; name='AIPolicy.Export'})) {
    if (-not $role.id) { Write-Host "    ⊘ $($role.name) role not found on app — skipping" -ForegroundColor DarkGray; continue }
    $hasRole = $userAssignments | Where-Object { $_.appRoleId -eq $role.id }
    if ($hasRole) {
        Write-Host "    ✓ $($role.name) role assigned" -ForegroundColor Green
    } else {
        Write-Host "    ⚠ $($role.name) role missing — assigning..." -ForegroundColor DarkYellow
        $body = @{ principalId=$currentUserId; resourceId=$apiSpId; appRoleId=$role.id } | ConvertTo-Json -Compress
        az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" --headers "Content-Type=application/json" --body $body -o none 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    ✓ $($role.name) role assigned" -ForegroundColor Green
        } else {
            Write-Host "    ⚠ $($role.name) role assignment failed (may already exist)" -ForegroundColor DarkYellow
        }
    }
}

# ── Step 2: Write UI auth config ─────────────────────────────────────

Write-Host ""
Write-Host "  Step 2: Writing dashboard auth config..." -ForegroundColor Yellow

$uiEnvDir = Join-Path $RepoRoot "src\aipolicyengine-ui"
$uiEnvFile = Join-Path $uiEnvDir ".env.production.local"
$uiEnvContent = @(
    "VITE_AZURE_TENANT_ID=$tenantId"
    "VITE_AZURE_CLIENT_ID=$apiAppId"
    "VITE_AZURE_API_APP_ID=$apiAppId"
    "VITE_AZURE_SCOPE=api://$apiAppId/access_as_user"
    "VITE_API_URL=https://$containerAppFqdn"
)
Set-Content -Path $uiEnvFile -Value $uiEnvContent -Encoding UTF8
Write-Host "    ✓ Wrote $uiEnvFile" -ForegroundColor Green

# ── Step 3: Build Docker image ───────────────────────────────────────

$imageRepository = "$acrLoginServer/aipolicy-api"
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "run-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
}
$imageTag = "${imageRepository}:$Tag"

Write-Host ""
Write-Host "  Step 3: Building container image..." -ForegroundColor Yellow
Write-Host "    Image: $imageTag" -ForegroundColor Gray

if ($SkipBuild) {
    Write-Host "    ⊘ Build skipped (-SkipBuild)" -ForegroundColor DarkGray
} else {
    docker build -t $imageTag -f "$RepoRoot/src/Dockerfile" "$RepoRoot/src"
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed." }
    Write-Host "    ✓ Image built" -ForegroundColor Green
}

# ── Step 4: Push to ACR ──────────────────────────────────────────────

Write-Host ""
Write-Host "  Step 4: Pushing to ACR..." -ForegroundColor Yellow

az acr login --name $acrName
if ($LASTEXITCODE -ne 0) { throw "ACR login failed." }

docker push $imageTag
if ($LASTEXITCODE -ne 0) { throw "Docker push failed." }

# Also tag as latest
docker tag $imageTag "${imageRepository}:latest"
docker push "${imageRepository}:latest"

Write-Host "    ✓ Image pushed to $acrLoginServer" -ForegroundColor Green

# ── Step 5: Update Container App ─────────────────────────────────────

Write-Host ""
Write-Host "  Step 5: Updating Container App..." -ForegroundColor Yellow

az containerapp update `
    --name $containerAppName `
    --resource-group $ResourceGroupName `
    --image $imageTag `
    -o none

if ($LASTEXITCODE -ne 0) { throw "Container App update failed." }

$newFqdn = az containerapp show --name $containerAppName --resource-group $ResourceGroupName --query "properties.configuration.ingress.fqdn" -o tsv
Write-Host "    ✓ Container App updated" -ForegroundColor Green
Write-Host "    URL: https://$newFqdn" -ForegroundColor Cyan

# ── Done ─────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  ✓ Deployment complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Container App: https://$newFqdn" -ForegroundColor Cyan
Write-Host "  Image:         $imageTag" -ForegroundColor Cyan
Write-Host ""
