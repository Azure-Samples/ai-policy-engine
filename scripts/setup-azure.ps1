#Requires -Modules Az.Accounts, Az.Resources, Az.ContainerRegistry

<#
.SYNOPSIS
    Deploys the Azure OpenAI Chargeback Environment from scratch.
.DESCRIPTION
    Automates: Resource Group, ACR, Entra App Registrations, Docker build/push,
    Bicep infrastructure, APIM configuration, and initial plan setup.
.PARAMETER Location
    Azure region for all resources (default: eastus2)
.PARAMETER WorkloadName
    Short name used as prefix for all resources (default: chrgbk)
.PARAMETER ResourceGroupName
    Resource group name (default: rg-chargeback-{Location})
.PARAMETER SkipBicep
    Skip the Bicep deployment (useful when re-running post-deploy steps)
.PARAMETER SkipDocker
    Skip Docker build/push (useful when image is already in ACR)
.PARAMETER SecondaryTenantId
    Optional second Entra tenant ID. When provided, Client 2 (multi-tenant) is also
    registered for billing under this tenant — useful for demonstrating per-tenant
    chargeback with a single client app serving multiple organizations.
.EXAMPLE
    .\setup-azure.ps1 -Location eastus2 -WorkloadName chrgbk
.EXAMPLE
    .\setup-azure.ps1 -Location eastus2 -WorkloadName chrgbk -SecondaryTenantId "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
#>
param(
    [string]$Location = "eastus2",
    [string]$WorkloadName = "chrgbk",
    [string]$ResourceGroupName = "",
    [string]$SecondaryTenantId = "",
    [switch]$SkipBicep,
    [switch]$SkipDocker
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $ResourceGroupName) { $ResourceGroupName = "rg-$WorkloadName-$Location" }
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

# Resource naming derived from workload
$workloadToken = ($WorkloadName.ToLowerInvariant() -replace '[^a-z0-9]', '')
if (-not $workloadToken) { throw "WorkloadName must contain at least one alphanumeric character." }

$ApimName = "apim-$WorkloadName"
$ContainerAppName = "ca-$WorkloadName"
$ContainerAppEnvName = "cae-$WorkloadName"
$RedisCacheName = "redis-$WorkloadName"
$CosmosAccountName = "cosmos-$WorkloadName"
$KeyVaultName = "kv-$workloadToken"
$LogAnalyticsWorkspaceName = "law-$workloadToken"
$AppInsightsName = "ai-$workloadToken"
$aiNameBase = "aisrv-$workloadToken"
if ($aiNameBase.Length -gt 58) { $aiNameBase = $aiNameBase.Substring(0, 58).TrimEnd('-') }
$storagePrefix = "st$workloadToken"
if ($storagePrefix.Length -gt 19) { $storagePrefix = $storagePrefix.Substring(0, 19) }

# These names will be resolved in Phase 2 (check existing, or generate new)
$AcrName = ""
$AiServiceName = ""
$StorageAccountName = ""

if ($ApimName.Length -gt 50) { $ApimName = $ApimName.Substring(0, 50).TrimEnd('-') }
if ($ContainerAppName.Length -gt 32) { $ContainerAppName = $ContainerAppName.Substring(0, 32).TrimEnd('-') }
if ($ContainerAppEnvName.Length -gt 32) { $ContainerAppEnvName = $ContainerAppEnvName.Substring(0, 32).TrimEnd('-') }
if ($RedisCacheName.Length -gt 63) { $RedisCacheName = $RedisCacheName.Substring(0, 63).TrimEnd('-') }
if ($CosmosAccountName.Length -gt 44) { $CosmosAccountName = $CosmosAccountName.Substring(0, 44).TrimEnd('-') }
if ($KeyVaultName.Length -gt 24) { $KeyVaultName = $KeyVaultName.Substring(0, 24).TrimEnd('-') }

Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   Azure OpenAI Chargeback - Full Environment Setup      ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Location:       $Location"
Write-Host "  Workload:       $WorkloadName"
Write-Host "  Resource Group: $ResourceGroupName"
Write-Host ""

# Tracking variables for deployment output
$deploymentOutput = @{}

function Ensure-ServicePrincipal {
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $spId = az ad sp show --id $AppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($spId)) {
        Write-Host "    ✓ Service principal exists for $DisplayName" -ForegroundColor Green
        return
    }

    Write-Host "    Creating service principal for $DisplayName..." -ForegroundColor Gray
    az ad sp create --id $AppId -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create service principal for $DisplayName ($AppId)." }

    $spReady = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Seconds 3
        $spId = az ad sp show --id $AppId --query "id" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($spId)) {
            $spReady = $true
            break
        }
    }
    if (-not $spReady) { throw "Service principal for $DisplayName ($AppId) was not discoverable after creation." }

    Write-Host "    ✓ Service principal ready for $DisplayName" -ForegroundColor Green
}

function Ensure-DelegatedScopeAndConsent {
    param(
        [Parameter(Mandatory = $true)][string]$ClientAppId,
        [Parameter(Mandatory = $true)][string]$ApiAppId,
        [Parameter(Mandatory = $true)][string]$ScopeId,
        [Parameter(Mandatory = $true)][string]$ClientDisplayName
    )

    # Check if this API permission already exists using the manifest directly
    $existingAccess = az ad app show --id $ClientAppId --query "requiredResourceAccess[?resourceAppId=='$ApiAppId'].resourceAccess[].id" -o tsv 2>$null
    $alreadyHasScope = ($existingAccess -split "`n" | ForEach-Object { $_.Trim() }) -contains $ScopeId

    if (-not $alreadyHasScope) {
        Write-Host "    Adding delegated scope permission for $ClientDisplayName..." -ForegroundColor Gray
        az ad app permission add --id $ClientAppId --api $ApiAppId --api-permissions "$ScopeId=Scope" -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to add delegated API permission for $ClientDisplayName." }
        Write-Host "    ✓ Delegated scope permission added" -ForegroundColor Green
    } else {
        Write-Host "    ✓ Delegated scope permission already present for $ClientDisplayName" -ForegroundColor Green
    }

    $consentGranted = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        az ad app permission admin-consent --id $ClientAppId -o none 2>$null
        if ($LASTEXITCODE -eq 0) {
            $consentGranted = $true
            break
        }

        Write-Host "    Admin consent attempt $attempt/10 failed for $ClientDisplayName — retrying in 5s..." -ForegroundColor DarkYellow
        Start-Sleep -Seconds 5
    }

    if (-not $consentGranted) { throw "Failed to grant admin consent for $ClientDisplayName after retries." }
    Write-Host "    ✓ Admin consent granted for $ClientDisplayName" -ForegroundColor Green
}

# ============================================================================
# Phase 1: Prerequisites Check
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 1: Prerequisites Check" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    # Verify az CLI is installed and logged in
    Write-Host "  Checking Azure CLI..." -ForegroundColor Gray
    $azVersion = az version 2>&1 | ConvertFrom-Json
    if (-not $azVersion) { throw "Azure CLI is not installed or not in PATH." }
    Write-Host "    ✓ Azure CLI $($azVersion.'azure-cli') found" -ForegroundColor Green

    $account = az account show 2>&1 | ConvertFrom-Json
    if (-not $account) { throw "Not logged in to Azure CLI. Run 'az login' first." }
    $subscriptionId = $account.id
    $tenantId = $account.tenantId
    Write-Host "    ✓ Logged in: $($account.user.name)" -ForegroundColor Green
    Write-Host "    ✓ Subscription: $($account.name) ($subscriptionId)" -ForegroundColor Green
    Write-Host "    ✓ Tenant: $tenantId" -ForegroundColor Green

    $deploymentOutput["subscriptionId"] = $subscriptionId
    $deploymentOutput["tenantId"] = $tenantId

    Write-Host "  Registering required Azure resource providers..." -ForegroundColor Gray
    $requiredProviders = @(
        "Microsoft.AlertsManagement",
        "Microsoft.ApiManagement",
        "Microsoft.App",
        "Microsoft.Cache",
        "Microsoft.CognitiveServices",
        "Microsoft.DocumentDB",
        "Microsoft.Insights",
        "Microsoft.KeyVault",
        "Microsoft.OperationalInsights",
        "Microsoft.Storage"
    )
    foreach ($providerNamespace in $requiredProviders) {
        $registrationState = az provider show --namespace $providerNamespace --query "registrationState" -o tsv 2>$null
        if ($registrationState -ne "Registered") {
            Write-Host "    Registering $providerNamespace..." -ForegroundColor Gray
            az provider register --namespace $providerNamespace --wait -o none
            if ($LASTEXITCODE -ne 0) { throw "Failed to register provider '$providerNamespace'." }
            Write-Host "    ✓ $providerNamespace registered" -ForegroundColor Green
        } else {
            Write-Host "    ✓ $providerNamespace already registered" -ForegroundColor Green
        }
    }

    # Verify Docker is running
    if (-not $SkipDocker) {
        Write-Host "  Checking Docker..." -ForegroundColor Gray
        $dockerInfo = docker info 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop and try again." }
        Write-Host "    ✓ Docker is running" -ForegroundColor Green
    } else {
        Write-Host "    ⊘ Docker check skipped (-SkipDocker)" -ForegroundColor DarkGray
    }

    # Verify .NET SDK is installed
    Write-Host "  Checking .NET SDK..." -ForegroundColor Gray
    $dotnetVersion = dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw ".NET SDK is not installed or not in PATH." }
    Write-Host "    ✓ .NET SDK $dotnetVersion found" -ForegroundColor Green

    Write-Host "  Phase 1 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Prerequisites check failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 2: Resource Group + ACR
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 2: Resource Group + Container Registry" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    # Create Resource Group (idempotent)
    Write-Host "  Creating resource group '$ResourceGroupName'..." -ForegroundColor Gray
    az group create --name $ResourceGroupName --location $Location -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create resource group." }
    Write-Host "    ✓ Resource group ready" -ForegroundColor Green

    # Check if ACR already exists in this RG, reuse if so
    $existingAcr = az acr list --resource-group $ResourceGroupName --query "[0].name" -o tsv 2>$null
    if ($existingAcr) {
        $AcrName = $existingAcr
        Write-Host "    ✓ Reusing existing ACR: $AcrName" -ForegroundColor Green
    } else {
        $AcrName = "acr$($WorkloadName)$(Get-Random -Minimum 100 -Maximum 999)"
        Write-Host "  Creating ACR '$AcrName'..." -ForegroundColor Gray
        az acr create --name $AcrName --resource-group $ResourceGroupName --sku Basic --admin-enabled true -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to create ACR." }
        Write-Host "    ✓ ACR '$AcrName' created" -ForegroundColor Green
    }

    Write-Host "  Ensuring ACR admin user is enabled..." -ForegroundColor Gray
    az acr update --name $AcrName --resource-group $ResourceGroupName --admin-enabled true -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to enable admin user on ACR '$AcrName'." }
    Write-Host "    ✓ ACR admin user enabled" -ForegroundColor Green

    # Check if Storage Account already exists in this RG, reuse if so
    $existingStorage = az storage account list --resource-group $ResourceGroupName --query "[0].name" -o tsv 2>$null
    if ($existingStorage) {
        $StorageAccountName = $existingStorage
        Write-Host "    ✓ Reusing existing Storage Account: $StorageAccountName" -ForegroundColor Green
    } else {
        $StorageAccountName = "$storagePrefix$(Get-Random -Minimum 10000 -Maximum 99999)"
        Write-Host "    Storage Account name: $StorageAccountName (will be created by Bicep)" -ForegroundColor Gray
    }

    # Check if AI Services account already exists in this RG, reuse if so
    $existingAiService = az cognitiveservices account list --resource-group $ResourceGroupName --query "[?kind=='AIServices'] | [0].name" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($existingAiService)) {
        $AiServiceName = $existingAiService
        Write-Host "    ✓ Reusing existing AI Services: $AiServiceName" -ForegroundColor Green
    } else {
        $AiServiceName = "$aiNameBase-$(Get-Random -Minimum 10000 -Maximum 99999)"
        Write-Host "    AI Services name: $AiServiceName (will be created by Bicep)" -ForegroundColor Gray
    }

    $deploymentOutput["acrName"] = $AcrName
    $deploymentOutput["resourceGroupName"] = $ResourceGroupName
    $deploymentOutput["apimName"] = $ApimName
    $deploymentOutput["containerAppName"] = $ContainerAppName
    $deploymentOutput["containerAppEnvName"] = $ContainerAppEnvName
    $deploymentOutput["redisCacheName"] = $RedisCacheName
    $deploymentOutput["cosmosAccountName"] = $CosmosAccountName
    $deploymentOutput["keyVaultName"] = $KeyVaultName
    $deploymentOutput["logAnalyticsWorkspaceName"] = $LogAnalyticsWorkspaceName
    $deploymentOutput["appInsightsName"] = $AppInsightsName
    $deploymentOutput["aiServiceName"] = $AiServiceName
    $deploymentOutput["storageAccountName"] = $StorageAccountName

    Write-Host "  Phase 2 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 2 failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 3: Entra App Registrations
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 3: Entra App Registrations" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    # --- API App ---
    Write-Host "  Creating API app registration 'Chargeback API'..." -ForegroundColor Gray

    # Check if it already exists
    $scopeId = ""
    $existingApiApp = az ad app list --display-name "Chargeback API" --query "[0]" 2>$null | ConvertFrom-Json
    if ($existingApiApp) {
        $apiAppId = $existingApiApp.appId
        $apiObjId = $existingApiApp.id
        Write-Host "    ✓ Reusing existing API app: $apiAppId" -ForegroundColor Green
    } else {
        $apiApp = az ad app create --display-name "Chargeback API" --sign-in-audience AzureADMultipleOrgs | ConvertFrom-Json
        $apiAppId = $apiApp.appId
        $apiObjId = $apiApp.id
        Write-Host "    ✓ API app created (multi-tenant): $apiAppId" -ForegroundColor Green

    }

    # Ensure the API app is multi-tenant (required for cross-tenant delegated auth)
    az ad app update --id $apiAppId --sign-in-audience AzureADMultipleOrgs 2>$null

    # Add Microsoft Graph openid permission (required for cross-tenant admin consent)
    $graphOpenIdId = "37f7f235-527c-4136-accd-4a02d197296e"
    Write-Host "  Ensuring Microsoft Graph openid permission on API app..." -ForegroundColor Gray
    $apiGraphAccess = az ad app show --id $apiAppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($apiGraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        az ad app permission add --id $apiAppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null
    }
    Write-Host "    ✓ Graph openid permission configured on API app" -ForegroundColor Green

    # Ensure Application ID URI is set
    az ad app update --id $apiAppId --identifier-uris "api://$apiAppId"
    if ($LASTEXITCODE -ne 0) { throw "Failed to set API app identifier URI." }
    Write-Host "    ✓ Identifier URI set: api://$apiAppId" -ForegroundColor Green

    # Resolve existing API scope, or create it if missing
    $scopeId = az ad app show --id $apiAppId --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($scopeId)) {
        $scopeId = [guid]::NewGuid().ToString()
        $scopeBody = @{
            api = @{
                oauth2PermissionScopes = @(@{
                    id                       = $scopeId
                    adminConsentDisplayName  = "Access Chargeback API"
                    adminConsentDescription  = "Allows the app to access the Chargeback API"
                    type                     = "Admin"
                    value                    = "access_as_user"
                    isEnabled                = $true
                })
            }
        } | ConvertTo-Json -Depth 5 -Compress

        # Write to temp file to avoid shell escaping issues
        $scopeFile = Join-Path $env:TEMP "scope-body.json"
        [System.IO.File]::WriteAllText($scopeFile, $scopeBody, [System.Text.UTF8Encoding]::new($false))
        az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$scopeFile" -o none
        Remove-Item $scopeFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { throw "Failed to expose API scope." }
        Write-Host "    ✓ API scope 'access_as_user' exposed" -ForegroundColor Green
    } else {
        Write-Host "    ✓ API scope 'access_as_user' already present" -ForegroundColor Green
    }

    Write-Host "  Ensuring API enterprise application exists..." -ForegroundColor Gray
    Ensure-ServicePrincipal -AppId $apiAppId -DisplayName "Chargeback API"

    # Ensure Chargeback.Export app role exists on the API app
    Write-Host "  Ensuring 'Chargeback.Export' app role..." -ForegroundColor Gray
    $existingExportRole = az ad app show --id $apiAppId --query "appRoles[?value=='Chargeback.Export'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingExportRole)) {
        $exportRoleId = [guid]::NewGuid().ToString()
        $currentRoles = az ad app show --id $apiAppId --query "appRoles" -o json 2>$null | ConvertFrom-Json
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $exportRoleId
            allowedMemberTypes = @("User", "Application")
            displayName        = "Chargeback Export"
            description        = "Allows the user or application to export chargeback billing summaries and audit trails"
            value              = "Chargeback.Export"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { throw "Failed to add Chargeback.Export app role." }
        Write-Host "    ✓ 'Chargeback.Export' app role created (ID: $exportRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'Chargeback.Export' app role already exists" -ForegroundColor Green
        $exportRoleId = $existingExportRole

        # Ensure allowedMemberTypes includes User (may have been created as Application-only)
        $currentAllowedTypes = az ad app show --id $apiAppId --query "appRoles[?value=='Chargeback.Export'] | [0].allowedMemberTypes" -o json 2>$null | ConvertFrom-Json
        if ($currentAllowedTypes -and ($currentAllowedTypes -notcontains "User")) {
            Write-Host "    Updating app role to allow User assignments..." -ForegroundColor Gray
            $currentRoles = az ad app show --id $apiAppId --query "appRoles" -o json 2>$null | ConvertFrom-Json
            foreach ($role in $currentRoles) {
                if ($role.value -eq "Chargeback.Export") {
                    $role.allowedMemberTypes = @("User", "Application")
                }
            }
            $roleBody = @{ appRoles = $currentRoles } | ConvertTo-Json -Depth 5 -Compress
            $roleFile = Join-Path $env:TEMP "app-role-body.json"
            [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
            az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none
            Remove-Item $roleFile -ErrorAction SilentlyContinue
            Write-Host "    ✓ App role updated to allow User + Application" -ForegroundColor Green
        }
    }

    # Ensure Chargeback.Admin app role exists
    Write-Host "  Ensuring 'Chargeback.Admin' app role..." -ForegroundColor Gray
    $existingAdminRole = az ad app show --id $apiAppId --query "appRoles[?value=='Chargeback.Admin'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingAdminRole)) {
        $adminRoleId = [guid]::NewGuid().ToString()
        $currentRoles = az ad app show --id $apiAppId --query "appRoles" -o json 2>$null | ConvertFrom-Json
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $adminRoleId
            allowedMemberTypes = @("User", "Application")
            displayName        = "Chargeback Admin"
            description        = "Allows the user or application to manage billing plans, client assignments, pricing, and usage policies"
            value              = "Chargeback.Admin"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { throw "Failed to add Chargeback.Admin app role." }
        Write-Host "    ✓ 'Chargeback.Admin' app role created (ID: $adminRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'Chargeback.Admin' app role already exists" -ForegroundColor Green
        $adminRoleId = $existingAdminRole
    }

    # Ensure Chargeback.Apim app role exists (for APIM managed identity → Container App auth)
    Write-Host "  Ensuring 'Chargeback.Apim' app role..." -ForegroundColor Gray
    $existingApimRole = az ad app show --id $apiAppId --query "appRoles[?value=='Chargeback.Apim'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingApimRole)) {
        $apimRoleId = [guid]::NewGuid().ToString()
        $currentRoles = az ad app show --id $apiAppId --query "appRoles" -o json 2>$null | ConvertFrom-Json
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $apimRoleId
            allowedMemberTypes = @("Application")
            displayName        = "APIM Service"
            description        = "Allows APIM to call the chargeback API precheck and log ingest endpoints"
            value              = "Chargeback.Apim"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { throw "Failed to add Chargeback.Apim app role." }
        Write-Host "    ✓ 'Chargeback.Apim' app role created (ID: $apimRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'Chargeback.Apim' app role already exists" -ForegroundColor Green
        $apimRoleId = $existingApimRole
    }

    # Assign Chargeback.Export and Chargeback.Admin roles to the deploying user
    Write-Host "  Assigning app roles to deploying user..." -ForegroundColor Gray
    $currentUserOid = az ad signed-in-user show --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($currentUserOid)) {
        $apiSpId = az ad sp show --id $apiAppId --query "id" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($apiSpId)) {
            foreach ($roleEntry in @(
                @{ Name = "Chargeback.Export"; Id = $exportRoleId },
                @{ Name = "Chargeback.Admin";  Id = $adminRoleId }
            )) {
                $existingAssignment = az rest --method GET `
                    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                    --query "value[?principalId=='$currentUserOid' && appRoleId=='$($roleEntry.Id)'] | [0].id" -o tsv 2>$null
                if ([string]::IsNullOrWhiteSpace($existingAssignment)) {
                    $assignBody = @{
                        principalId = $currentUserOid
                        resourceId  = $apiSpId
                        appRoleId   = $roleEntry.Id
                    } | ConvertTo-Json -Compress
                    $assignFile = Join-Path $env:TEMP "role-assign-body.json"
                    [System.IO.File]::WriteAllText($assignFile, $assignBody, [System.Text.UTF8Encoding]::new($false))
                    az rest --method POST `
                        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                        --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null
                    Remove-Item $assignFile -ErrorAction SilentlyContinue
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "    ✓ $($roleEntry.Name) role assigned to current user" -ForegroundColor Green
                    } else {
                        Write-Host "    ⚠ Could not assign $($roleEntry.Name) — assign manually in Entra ID" -ForegroundColor DarkYellow
                    }
                } else {
                    Write-Host "    ✓ $($roleEntry.Name) role already assigned to current user" -ForegroundColor Green
                }
            }
        }
    } else {
        Write-Host "    ⚠ Could not determine current user — assign roles manually in Entra ID" -ForegroundColor DarkYellow
    }

    $deploymentOutput["apiAppId"] = $apiAppId
    $deploymentOutput["apiObjId"] = $apiObjId
    $deploymentOutput["adminRoleId"] = $adminRoleId

    # --- Client App 1 ---
    Write-Host "  Creating client app 'Chargeback Sample Client'..." -ForegroundColor Gray

    $existingClient1 = az ad app list --display-name "Chargeback Sample Client" --query "[0]" 2>$null | ConvertFrom-Json
    if ($existingClient1) {
        $client1AppId = $existingClient1.appId
        $client1ObjId = $existingClient1.id
        Write-Host "    ✓ Reusing existing client app 1: $client1AppId" -ForegroundColor Green
    } else {
        $client1 = az ad app create --display-name "Chargeback Sample Client" --sign-in-audience AzureADMyOrg | ConvertFrom-Json
        $client1AppId = $client1.appId
        $client1ObjId = $client1.id
        Write-Host "    ✓ Client app 1 created: $client1AppId" -ForegroundColor Green
    }

    Ensure-ServicePrincipal -AppId $client1AppId -DisplayName "Chargeback Sample Client"
    Ensure-DelegatedScopeAndConsent -ClientAppId $client1AppId -ApiAppId $apiAppId -ScopeId $scopeId -ClientDisplayName "Chargeback Sample Client"

    # Add Microsoft Graph openid permission to Client 1
    $client1GraphAccess = az ad app show --id $client1AppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($client1GraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        az ad app permission add --id $client1AppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null
    }

    # Create client secret for client 1
    $client1Secret = az ad app credential reset --id $client1AppId --display-name "setup-script" --years 1 --query "password" -o tsv 2>$null
    if ($client1Secret) {
        Write-Host "    ✓ Client 1 secret created" -ForegroundColor Green
    }

    # Assign Chargeback.Admin to client 1 SP (used by Phase 9 for plan seeding)
    $client1SpId = az ad sp show --id $client1AppId --query "id" -o tsv 2>$null
    $apiSpId = az ad sp show --id $apiAppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($client1SpId) -and -not [string]::IsNullOrWhiteSpace($apiSpId)) {
        $existingAdminAssign = az rest --method GET `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
            --query "value[?principalId=='$client1SpId' && appRoleId=='$adminRoleId'] | [0].id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingAdminAssign)) {
            $assignBody = @{ principalId = $client1SpId; resourceId = $apiSpId; appRoleId = $adminRoleId } | ConvertTo-Json -Compress
            $assignFile = Join-Path $env:TEMP "client1-admin-role.json"
            [System.IO.File]::WriteAllText($assignFile, $assignBody, [System.Text.UTF8Encoding]::new($false))
            az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null
            Remove-Item $assignFile -ErrorAction SilentlyContinue
            Write-Host "    ✓ Chargeback.Admin role assigned to Client 1 SP" -ForegroundColor Green
        } else {
            Write-Host "    ✓ Chargeback.Admin role already assigned to Client 1 SP" -ForegroundColor Green
        }
    }

    $deploymentOutput["client1AppId"] = $client1AppId
    $deploymentOutput["client1ObjId"] = $client1ObjId
    $deploymentOutput["client1Secret"] = $client1Secret

    # --- Client App 2 (Multi-tenant — demonstrates per-tenant billing) ---
    Write-Host "  Creating client app 'Chargeback Demo Client 2' (multi-tenant)..." -ForegroundColor Gray

    $existingClient2 = az ad app list --display-name "Chargeback Demo Client 2" --query "[0]" 2>$null | ConvertFrom-Json
    if ($existingClient2) {
        $client2AppId = $existingClient2.appId
        $client2ObjId = $existingClient2.id
        Write-Host "    ✓ Reusing existing client app 2: $client2AppId" -ForegroundColor Green
        # Ensure it's multi-tenant
        az ad app update --id $client2AppId --sign-in-audience AzureADMultipleOrgs 2>$null
        Write-Host "    ✓ Client 2 updated to multi-tenant (AzureADMultipleOrgs)" -ForegroundColor Green
    } else {
        $client2 = az ad app create --display-name "Chargeback Demo Client 2" --sign-in-audience AzureADMultipleOrgs | ConvertFrom-Json
        $client2AppId = $client2.appId
        $client2ObjId = $client2.id
        Write-Host "    ✓ Client app 2 created (multi-tenant): $client2AppId" -ForegroundColor Green
    }

    Ensure-ServicePrincipal -AppId $client2AppId -DisplayName "Chargeback Demo Client 2"
    Ensure-DelegatedScopeAndConsent -ClientAppId $client2AppId -ApiAppId $apiAppId -ScopeId $scopeId -ClientDisplayName "Chargeback Demo Client 2"

    # Add Microsoft Graph openid permission to Client 2
    $client2GraphAccess = az ad app show --id $client2AppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($client2GraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        az ad app permission add --id $client2AppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null
    }

    # Enable public client flow and add localhost redirect URI for interactive auth (cross-tenant demo)
    az ad app update --id $client2AppId --public-client-redirect-uris "http://localhost:29783" --enable-id-token-issuance true 2>$null
    Write-Host "    ✓ Client 2 public client redirect URI configured (http://localhost:29783)" -ForegroundColor Green

    # Create client secret for client 2
    $client2Secret = az ad app credential reset --id $client2AppId --display-name "setup-script" --years 1 --query "password" -o tsv 2>$null
    if ($client2Secret) {
        Write-Host "    ✓ Client 2 secret created" -ForegroundColor Green
    }

    $deploymentOutput["client2AppId"] = $client2AppId
    $deploymentOutput["client2ObjId"] = $client2ObjId
    $deploymentOutput["client2Secret"] = $client2Secret

    Write-Host "  Phase 3 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 3 failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 4: Docker Build + Push
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 4: Docker Build + Push" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

$imageRepository = "$($AcrName).azurecr.io/chargeback-api"
$runTag = "run-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$imageTag = if ($SkipDocker) { "${imageRepository}:latest" } else { "${imageRepository}:$runTag" }
$deploymentOutput["containerImage"] = $imageTag

if ($SkipDocker) {
    Write-Host "    ⊘ Docker build/push skipped (-SkipDocker)" -ForegroundColor DarkGray
    Write-Host ""
} else {
    try {
        Write-Host "  Writing dashboard auth config for UI build..." -ForegroundColor Gray
        $uiEnvFile = Join-Path $RepoRoot "src\chargeback-ui\.env.production.local"
        $uiEnvLines = @(
            "# Auto-generated by scripts/setup-azure.ps1"
            "VITE_AZURE_CLIENT_ID=$apiAppId"
            "VITE_AZURE_TENANT_ID=$tenantId"
            "VITE_AZURE_API_APP_ID=$apiAppId"
            "VITE_AZURE_AUTHORITY=https://login.microsoftonline.com/$tenantId"
            "VITE_AZURE_SCOPE=api://$apiAppId/access_as_user"
        )
        Set-Content -Path $uiEnvFile -Value $uiEnvLines -Encoding UTF8
        $deploymentOutput["dashboardUiEnvFile"] = $uiEnvFile
        Write-Host "    ✓ UI auth config written: $uiEnvFile" -ForegroundColor Green

        Write-Host "  Logging into ACR '$AcrName'..." -ForegroundColor Gray
        $acrLoginOk = $false
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            az acr login --name $AcrName 2>$null
            if ($LASTEXITCODE -eq 0) {
                $acrLoginOk = $true
                break
            }

            Write-Host "    ACR login attempt $attempt/5 failed — retrying in 10s..." -ForegroundColor DarkYellow
            Start-Sleep -Seconds 10
        }
        if (-not $acrLoginOk) {
            Write-Host "    ACR diagnostic info:" -ForegroundColor Red
            az acr show --name $AcrName --resource-group $ResourceGroupName --query "{name:name,loginServer:loginServer,adminUserEnabled:adminUserEnabled}" -o table 2>$null
            throw "ACR login failed after retries."
        }
        Write-Host "    ✓ ACR login successful" -ForegroundColor Green

        Write-Host "  Building Docker image..." -ForegroundColor Gray
        Write-Host "    Image: $imageTag" -ForegroundColor Gray
        docker build -t $imageTag -f "$RepoRoot/src/Dockerfile" "$RepoRoot/src"
        if ($LASTEXITCODE -ne 0) { throw "Docker build failed." }
        Write-Host "    ✓ Image built" -ForegroundColor Green

        Write-Host "  Pushing image to ACR..." -ForegroundColor Gray
        docker push $imageTag
        if ($LASTEXITCODE -ne 0) { throw "Docker push failed." }
        Write-Host "    ✓ Image pushed to ACR" -ForegroundColor Green

        Write-Host "  Phase 4 complete ✓" -ForegroundColor Green
        Write-Host ""
    } catch {
        Write-Host "  ✗ Phase 4 failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# ============================================================================
# Phase 5: Bicep Deployment
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 5: Bicep Infrastructure Deployment" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

if ($SkipBicep) {
    Write-Host "    ⊘ Bicep deployment skipped (-SkipBicep)" -ForegroundColor DarkGray
    Write-Host ""
} else {
    try {
        Write-Host "  Retrieving ACR credentials..." -ForegroundColor Gray
        $acrPassword = az acr credential show --name $AcrName --query "passwords[0].value" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Failed to get ACR credentials." }
        Write-Host "    ✓ ACR credentials retrieved" -ForegroundColor Green

        Write-Host "  Checking soft-deleted resource collisions..." -ForegroundColor Gray
        $deletedApim = az apim deletedservice list --query "[?name=='$ApimName'] | [0].name" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($deletedApim)) {
            Write-Host "    Purging soft-deleted APIM '$ApimName'..." -ForegroundColor Gray
            az apim deletedservice purge --name $ApimName --location $Location -o none
            if ($LASTEXITCODE -ne 0) { throw "Failed to purge soft-deleted APIM service '$ApimName'." }
            Write-Host "    ✓ Purged APIM soft-delete record" -ForegroundColor Green
        } else {
            Write-Host "    ✓ No APIM soft-delete collision" -ForegroundColor Green
        }

        $deletedKeyVault = az keyvault list-deleted --query "[?name=='$KeyVaultName'] | [0].name" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($deletedKeyVault)) {
            Write-Host "    Purging soft-deleted Key Vault '$KeyVaultName'..." -ForegroundColor Gray
            az keyvault purge --name $KeyVaultName -o none
            if ($LASTEXITCODE -ne 0) { throw "Failed to purge soft-deleted Key Vault '$KeyVaultName'." }
            Write-Host "    ✓ Purged Key Vault soft-delete record" -ForegroundColor Green
        } else {
            Write-Host "    ✓ No Key Vault soft-delete collision" -ForegroundColor Green
        }

        Write-Host "  Selecting Azure AI Services account name..." -ForegroundColor Gray
        # If we already discovered an existing AI Services account in Phase 2, reuse it
        $existingAiInRg = az cognitiveservices account list --resource-group $ResourceGroupName --query "[?kind=='AIServices'] | [0].name" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($existingAiInRg)) {
            $AiServiceName = $existingAiInRg
            Write-Host "    ✓ Reusing existing AI Services: $AiServiceName" -ForegroundColor Green
        } else {
            # Generate a new unique name that doesn't collide with active or soft-deleted resources
            if ([string]::IsNullOrWhiteSpace($AiServiceName)) {
                $AiServiceName = "$aiNameBase-$(Get-Random -Minimum 10000 -Maximum 99999)"
            }
            $activeAiCollision = ""
            $deletedAiCollision = ""
            for ($attempt = 1; $attempt -le 20; $attempt++) {
                $activeAiCollision = az cognitiveservices account list --query "[?name=='$AiServiceName'] | [0].name" -o tsv 2>$null
                $deletedAiCollision = az cognitiveservices account list-deleted --query "[?name=='$AiServiceName'] | [0].name" -o tsv 2>$null
                if ([string]::IsNullOrWhiteSpace($activeAiCollision) -and [string]::IsNullOrWhiteSpace($deletedAiCollision)) {
                    break
                }

                Write-Host "    Name collision detected for '$AiServiceName' (attempt $attempt) — generating a new name..." -ForegroundColor DarkYellow
                $AiServiceName = "$aiNameBase-$(Get-Random -Minimum 10000 -Maximum 99999)"
            }
            if (-not [string]::IsNullOrWhiteSpace($activeAiCollision) -or -not [string]::IsNullOrWhiteSpace($deletedAiCollision)) {
                throw "Could not find an available Azure AI Services account name after 20 attempts."
            }
        }
        $deploymentOutput["aiServiceName"] = $AiServiceName
        Write-Host "    ✓ Azure AI Services name: $AiServiceName" -ForegroundColor Green

        Write-Host "  Starting Bicep deployment (this may take 30-60 minutes for APIM)..." -ForegroundColor Magenta
        Write-Host "    Template: infra/bicep/main.bicep" -ForegroundColor Gray

        $bicepResult = az deployment group create `
            --resource-group $ResourceGroupName `
            --template-file "$RepoRoot/infra/bicep/main.bicep" `
            --parameters "$RepoRoot/infra/bicep/parameter.json" `
            --parameters `
                location=$Location `
                workloadName=$WorkloadName `
                apimInstanceName=$ApimName `
                keyVaultName=$KeyVaultName `
                redisCacheName=$RedisCacheName `
                cosmosAccountName=$CosmosAccountName `
                logAnalyticsWorkspaceName=$LogAnalyticsWorkspaceName `
                appInsightsName=$AppInsightsName `
                storageAccountName=$StorageAccountName `
                aiServiceName=$AiServiceName `
                containerAppName=$ContainerAppName `
                containerAppEnvName=$ContainerAppEnvName `
                containerImage=$imageTag `
                acrLoginServer="$($AcrName).azurecr.io" `
                acrUsername=$AcrName `
                acrPassword=$acrPassword `
            --query "properties.outputs" -o json --only-show-errors 2>&1

        if ($LASTEXITCODE -ne 0) {
            Write-Host "    Bicep deployment error details:" -ForegroundColor Red
            Write-Host $bicepResult -ForegroundColor DarkRed
            throw "Bicep deployment failed. See error output above."
        }

        $bicepResultText = ($bicepResult | Out-String).Trim()
        $jsonStart = $bicepResultText.IndexOf('{')
        $jsonEnd = $bicepResultText.LastIndexOf('}')
        if ($jsonStart -lt 0 -or $jsonEnd -lt $jsonStart) {
            Write-Host "    Unexpected deployment output:" -ForegroundColor Red
            Write-Host $bicepResultText -ForegroundColor DarkRed
            throw "Bicep deployment succeeded but output was not valid JSON."
        }

        $bicepJson = $bicepResultText.Substring($jsonStart, $jsonEnd - $jsonStart + 1)
        $bicepOutputs = $bicepJson | ConvertFrom-Json
        Write-Host "    ✓ Bicep deployment complete" -ForegroundColor Green

        if ($bicepOutputs.containerAppUrlInfo) {
            $deploymentOutput["containerAppUrl"] = $bicepOutputs.containerAppUrlInfo.value
        }
        if ($bicepOutputs.appInsightsConnectionString) {
            $deploymentOutput["appInsightsConnectionString"] = $bicepOutputs.appInsightsConnectionString.value
        }
        if ($bicepOutputs.logAnalyticsWorkbookUrl) {
            $deploymentOutput["logAnalyticsWorkbookUrl"] = $bicepOutputs.logAnalyticsWorkbookUrl.value
        }

        Write-Host "  Phase 5 complete ✓" -ForegroundColor Green
        Write-Host ""
    } catch {
        Write-Host "  ✗ Phase 5 failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# ============================================================================
# Phase 6: Post-Deployment Configuration
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 6: Post-Deployment Configuration" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    # Get Container App URL if not already set from Bicep outputs
    if (-not $deploymentOutput["containerAppUrl"]) {
        Write-Host "  Retrieving Container App URL..." -ForegroundColor Gray
        $containerAppUrl = az containerapp show --name $ContainerAppName --resource-group $ResourceGroupName --query "properties.configuration.ingress.fqdn" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Failed to get Container App URL." }
        $deploymentOutput["containerAppUrl"] = $containerAppUrl
    } else {
        $containerAppUrl = $deploymentOutput["containerAppUrl"]
    }
    Write-Host "    ✓ Container App URL: $containerAppUrl" -ForegroundColor Green

    # Redis uses Entra ID (managed identity) authentication — no access key needed.
    # The connection string is set in the Bicep template without a password, and the
    # Container App's managed identity is granted "Data Owner" via an access policy assignment.
    Write-Host "    ✓ Redis uses Entra ID auth (managed identity) — no key required" -ForegroundColor Green

    # Configure Cosmos DB connection string
    Write-Host "  Configuring Cosmos DB connection..." -ForegroundColor Gray
    $cosmosEndpoint = az cosmosdb show --name $CosmosAccountName --resource-group $ResourceGroupName --query "documentEndpoint" -o tsv 2>$null
    if ($cosmosEndpoint) {
        az containerapp update --name $ContainerAppName --resource-group $ResourceGroupName --set-env-vars "ConnectionStrings__chargeback=$cosmosEndpoint" -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to update Container App Cosmos connection." }
        Write-Host "    ✓ Cosmos DB connection configured: $cosmosEndpoint" -ForegroundColor Green
    } else {
        Write-Host "    ⚠ Cosmos DB account not found — skipping connection string" -ForegroundColor DarkYellow
    }

    # Assign Cosmos DB data-plane RBAC to Container App managed identity
    Write-Host "  Assigning Cosmos DB data contributor role to Container App..." -ForegroundColor Gray
    $containerAppPrincipal = az containerapp show --name $ContainerAppName --resource-group $ResourceGroupName --query "identity.principalId" -o tsv
    $cosmosAccountId = az cosmosdb show --name $CosmosAccountName --resource-group $ResourceGroupName --query "id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($containerAppPrincipal)) {
        Write-Host "    ⚠ Container App managed identity not found — cannot assign Cosmos role" -ForegroundColor DarkYellow
    } elseif ([string]::IsNullOrWhiteSpace($cosmosAccountId)) {
        Write-Host "    ⚠ Cosmos DB account '$CosmosAccountName' not found — cannot assign role" -ForegroundColor DarkYellow
    } else {
        # Cosmos DB Built-in Data Contributor — fully qualified role definition ID
        $cosmosRoleDefId = "$cosmosAccountId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
        # Check if role assignment already exists (idempotent for re-runs)
        $existingCosmosRole = az cosmosdb sql role assignment list `
            --account-name $CosmosAccountName `
            --resource-group $ResourceGroupName `
            --query "[?principalId=='$containerAppPrincipal' && contains(roleDefinitionId, '00000000-0000-0000-0000-000000000002')]" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingCosmosRole)) {
            Write-Host "    Creating Cosmos DB role assignment..." -ForegroundColor Gray
            Write-Host "      Principal: $containerAppPrincipal" -ForegroundColor Gray
            Write-Host "      Scope: $cosmosAccountId" -ForegroundColor Gray
            az cosmosdb sql role assignment create `
                --account-name $CosmosAccountName `
                --resource-group $ResourceGroupName `
                --role-definition-id $cosmosRoleDefId `
                --principal-id $containerAppPrincipal `
                --scope $cosmosAccountId `
                -o none
            if ($LASTEXITCODE -ne 0) {
                Write-Host "    ✗ Cosmos DB role assignment failed — check permissions" -ForegroundColor Red
                Write-Host "      You may need to manually run:" -ForegroundColor DarkYellow
                Write-Host "      az cosmosdb sql role assignment create --account-name $CosmosAccountName --resource-group $ResourceGroupName --role-definition-id '$cosmosRoleDefId' --principal-id $containerAppPrincipal --scope '$cosmosAccountId'" -ForegroundColor DarkYellow
            } else {
                Write-Host "    ✓ Cosmos DB Data Contributor role assigned" -ForegroundColor Green
            }
        } else {
            Write-Host "    ✓ Cosmos DB Data Contributor role already assigned" -ForegroundColor Green
        }
    }

    # Configure AzureAd settings for JWT authentication on export endpoints
    Write-Host "  Configuring AzureAd JWT auth settings on Container App..." -ForegroundColor Gray
    az containerapp update --name $ContainerAppName --resource-group $ResourceGroupName `
        --set-env-vars `
            "AzureAd__Instance=https://login.microsoftonline.com/" `
            "AzureAd__TenantId=$tenantId" `
            "AzureAd__ClientId=$apiAppId" `
            "AzureAd__Audience=api://$apiAppId" `
        -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to update Container App AzureAd config." }
    Write-Host "    ✓ AzureAd JWT auth configured (TenantId, ClientId, Audience)" -ForegroundColor Green

    # Assign Cognitive Services User role to APIM managed identity
    Write-Host "  Assigning Cognitive Services User role to APIM..." -ForegroundColor Gray
    $apimPrincipal = az apim show --name $ApimName --resource-group $ResourceGroupName --query "identity.principalId" -o tsv
    if ($LASTEXITCODE -ne 0) { throw "Failed to get APIM principal ID." }
    $aiSvcId = az cognitiveservices account list --resource-group $ResourceGroupName --query "[?kind=='AIServices'].id | [0]" -o tsv
    if ($aiSvcId) {
        az role assignment create --assignee $apimPrincipal --role "Cognitive Services User" --scope $aiSvcId -o none 2>$null
        Write-Host "    ✓ Cognitive Services User role assigned to APIM" -ForegroundColor Green
    } else {
        Write-Host "    ⚠ No AI Services account found — skipping role assignment" -ForegroundColor DarkYellow
    }

    # Assign Chargeback.Apim app role to APIM managed identity
    Write-Host "  Assigning 'Chargeback.Apim' app role to APIM managed identity..." -ForegroundColor Gray
    $apiSpId = az ad sp show --id $apiAppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($apiSpId) -and -not [string]::IsNullOrWhiteSpace($apimPrincipal)) {
        $apimSpId = az ad sp show --id $apimPrincipal --query "id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($apimSpId)) {
            $apimSpId = $apimPrincipal
        }
        $existingApimAssignment = az rest --method GET `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
            --query "value[?principalId=='$apimSpId' && appRoleId=='$apimRoleId'] | [0].id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingApimAssignment)) {
            $assignBody = @{
                principalId = $apimSpId
                resourceId  = $apiSpId
                appRoleId   = $apimRoleId
            } | ConvertTo-Json -Compress
            $assignFile = Join-Path $env:TEMP "apim-role-assign.json"
            [System.IO.File]::WriteAllText($assignFile, $assignBody, [System.Text.UTF8Encoding]::new($false))
            az rest --method POST `
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null
            Remove-Item $assignFile -ErrorAction SilentlyContinue
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    ✓ Chargeback.Apim role assigned to APIM managed identity" -ForegroundColor Green
            } else {
                Write-Host "    ⚠ Could not assign Chargeback.Apim role to APIM — assign manually" -ForegroundColor DarkYellow
            }
        } else {
            Write-Host "    ✓ Chargeback.Apim role already assigned to APIM managed identity" -ForegroundColor Green
        }
    } else {
        Write-Host "    ⚠ Could not resolve service principals — assign Chargeback.Apim role manually" -ForegroundColor DarkYellow
    }

    $deploymentOutput["apimName"] = $ApimName

    Write-Host "  Phase 6 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 6 failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 7: APIM Configuration
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 7: APIM Configuration" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    # Create named values
    Write-Host "  Creating APIM named values..." -ForegroundColor Gray

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id EntraTenantId --display-name "EntraTenantId" --value $tenantId -o none 2>$null
    Write-Host "    ✓ EntraTenantId" -ForegroundColor Green

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id ExpectedAudience --display-name "ExpectedAudience" --value "api://$apiAppId" -o none 2>$null
    Write-Host "    ✓ ExpectedAudience = api://$apiAppId" -ForegroundColor Green

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id ContainerAppUrl --display-name "ContainerAppUrl" --value "https://$containerAppUrl" -o none 2>$null
    Write-Host "    ✓ ContainerAppUrl = https://$containerAppUrl" -ForegroundColor Green

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id ContainerAppAudience --display-name "ContainerAppAudience" --value "api://$apiAppId" -o none 2>$null
    Write-Host "    ✓ ContainerAppAudience = api://$apiAppId" -ForegroundColor Green

    # Disable subscription required on the OpenAI API
    Write-Host "  Disabling subscription requirement on OpenAI API..." -ForegroundColor Gray
    az apim api update --resource-group $ResourceGroupName --service-name $ApimName `
        --api-id azure-openai-api --subscription-required false -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to update API subscription setting." }
    Write-Host "    ✓ Subscription requirement disabled" -ForegroundColor Green

    # Fix API path and backend service URL
    Write-Host "  Configuring API path and backend..." -ForegroundColor Gray
    $aiEndpoint = az cognitiveservices account list --resource-group $ResourceGroupName `
        --query "[?kind=='AIServices'].properties.endpoint | [0]" -o tsv
    if ($aiEndpoint) {
        az apim api update --resource-group $ResourceGroupName --service-name $ApimName `
            --api-id azure-openai-api --set path=openai --service-url "${aiEndpoint}openai" -o none
        Write-Host "    ✓ API path set to /openai, backend = ${aiEndpoint}openai" -ForegroundColor Green
    } else {
        Write-Host "    ⚠ No AI endpoint found — skipping API path update" -ForegroundColor DarkYellow
    }

    # Upload APIM policy from entra-jwt-policy.xml
    Write-Host "  Uploading APIM JWT validation policy..." -ForegroundColor Gray
    $policyXml = Get-Content "$RepoRoot/policies/entra-jwt-policy.xml" -Raw
    $body = @{ properties = @{ format = "rawxml"; value = $policyXml } } | ConvertTo-Json -Depth 3 -Compress
    $policyFile = Join-Path $env:TEMP "apim-policy.json"
    [System.IO.File]::WriteAllText($policyFile, $body, [System.Text.UTF8Encoding]::new($false))

    $policyUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.ApiManagement/service/$ApimName/apis/azure-openai-api/policies/policy?api-version=2022-08-01"
    az rest --method PUT --uri $policyUri --headers "Content-Type=application/json" --body "@$policyFile" -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to upload APIM policy." }
    Remove-Item $policyFile -ErrorAction SilentlyContinue
    Write-Host "    ✓ APIM policy uploaded (entra-jwt-policy.xml)" -ForegroundColor Green

    Write-Host "  Phase 7 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 7 failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 8: Entra Redirect URIs
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 8: Entra Redirect URIs" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    Write-Host "  Setting SPA redirect URIs on client app 1..." -ForegroundColor Gray
    $redirectBody = @{
        spa = @{
            redirectUris = @(
                "https://$containerAppUrl/"
                "http://localhost:5173/"
            )
        }
    } | ConvertTo-Json -Depth 3 -Compress
    $redirectFile = Join-Path $env:TEMP "redirect-body.json"
    [System.IO.File]::WriteAllText($redirectFile, $redirectBody, [System.Text.UTF8Encoding]::new($false))

    az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$client1ObjId" `
        --headers "Content-Type=application/json" --body "@$redirectFile" -o none
    Remove-Item $redirectFile -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { throw "Failed to set redirect URIs on client app 1." }
    Write-Host "    ✓ Redirect URIs: https://$containerAppUrl/, http://localhost:5173/" -ForegroundColor Green

    Write-Host "  Phase 8 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 8 failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Phase 9: Initial Plan Setup
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 9: Initial Plan Setup" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

try {
    $baseUrl = "https://$containerAppUrl"

    # Acquire an access token using client1 credentials (has Chargeback.Admin role)
    Write-Host "  Acquiring access token via client credentials..." -ForegroundColor Gray
    $tokenEndpoint = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"
    $tokenBody = @{
        grant_type    = "client_credentials"
        client_id     = $client1AppId
        client_secret = $client1Secret
        scope         = "api://$apiAppId/.default"
    }
    try {
        $tokenResponse = Invoke-RestMethod -Uri $tokenEndpoint -Method Post -Body $tokenBody -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop
        $accessToken = $tokenResponse.access_token
    } catch {
        throw "Failed to acquire access token via client credentials: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($accessToken)) {
        throw "Token response did not contain an access token."
    }
    $authHeaders = @{ Authorization = "Bearer $accessToken" }
    Write-Host "    ✓ Access token acquired" -ForegroundColor Green

    # Wait for Container App to be responsive
    Write-Host "  Waiting for Container App to be ready..." -ForegroundColor Gray
    $maxRetries = 12
    $retryCount = 0
    $ready = $false
    while (-not $ready -and $retryCount -lt $maxRetries) {
        try {
            $healthCheck = Invoke-RestMethod -Uri "$baseUrl/api/plans" -Method Get -Headers $authHeaders -TimeoutSec 10 -ErrorAction Stop
            $ready = $true
        } catch {
            $retryCount++
            Write-Host "    Attempt $retryCount/$maxRetries — waiting 10s..." -ForegroundColor DarkGray
            Start-Sleep -Seconds 10
        }
    }
    if (-not $ready) { throw "Container App not responding after $maxRetries attempts." }
    Write-Host "    ✓ Container App is ready" -ForegroundColor Green

    $plansResponse = Invoke-RestMethod -Uri "$baseUrl/api/plans" -Method Get -Headers $authHeaders -TimeoutSec 15
    $existingPlans = @($plansResponse.plans)

    # Ensure Enterprise plan
    Write-Host "  Ensuring Enterprise plan..." -ForegroundColor Gray
    $entPlanBody = @{
        name                   = "Enterprise"
        monthlyRate            = 999.99
        monthlyTokenQuota      = 10000000
        tokensPerMinuteLimit   = 200000
        requestsPerMinuteLimit = 120
        allowOverbilling       = $true
        costPerMillionTokens   = 10.0
    } | ConvertTo-Json
    $enterprisePlans = @($existingPlans | Where-Object { $_.name -and $_.name.Trim() -ieq "Enterprise" })
    if ($enterprisePlans.Count -gt 1) {
        Write-Host "    ⚠ Multiple Enterprise plans found — using the first match" -ForegroundColor DarkYellow
    }
    $enterprisePlan = $enterprisePlans | Select-Object -First 1
    if ($enterprisePlan) {
        $entPlan = Invoke-RestMethod -Uri "$baseUrl/api/plans/$($enterprisePlan.id)" -Method Put -Body $entPlanBody -ContentType "application/json" -Headers $authHeaders
        Write-Host "    ✓ Enterprise plan updated (ID: $($entPlan.id))" -ForegroundColor Green
    } else {
        $entPlan = Invoke-RestMethod -Uri "$baseUrl/api/plans" -Method Post -Body $entPlanBody -ContentType "application/json" -Headers $authHeaders
        Write-Host "    ✓ Enterprise plan created (ID: $($entPlan.id))" -ForegroundColor Green
    }

    # Ensure Starter plan
    Write-Host "  Ensuring Starter plan..." -ForegroundColor Gray
    $startPlanBody = @{
        name                   = "Starter"
        monthlyRate            = 49.99
        monthlyTokenQuota      = 500
        tokensPerMinuteLimit   = 1000
        requestsPerMinuteLimit = 10
        allowOverbilling       = $false
        costPerMillionTokens   = 0
    } | ConvertTo-Json
    $starterPlans = @($existingPlans | Where-Object { $_.name -and $_.name.Trim() -ieq "Starter" })
    if ($starterPlans.Count -gt 1) {
        Write-Host "    ⚠ Multiple Starter plans found — using the first match" -ForegroundColor DarkYellow
    }
    $starterPlan = $starterPlans | Select-Object -First 1
    if ($starterPlan) {
        $startPlan = Invoke-RestMethod -Uri "$baseUrl/api/plans/$($starterPlan.id)" -Method Put -Body $startPlanBody -ContentType "application/json" -Headers $authHeaders
        Write-Host "    ✓ Starter plan updated (ID: $($startPlan.id))" -ForegroundColor Green
    } else {
        $startPlan = Invoke-RestMethod -Uri "$baseUrl/api/plans" -Method Post -Body $startPlanBody -ContentType "application/json" -Headers $authHeaders
        Write-Host "    ✓ Starter plan created (ID: $($startPlan.id))" -ForegroundColor Green
    }

    # Assign clients to plans
    # Client 1 is single-tenant — tenantId matches the deployment tenant
    Write-Host "  Assigning clients to plans..." -ForegroundColor Gray
    $client1Body = @{ planId = $entPlan.id; displayName = "Chargeback Sample Client" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/api/clients/$client1AppId/$tenantId" -Method Put -Body $client1Body -ContentType "application/json" -Headers $authHeaders | Out-Null
    Write-Host "    ✓ Client 1 → Enterprise plan (tenant: $tenantId)" -ForegroundColor Green

    # Client 2 is multi-tenant — register with the deployment tenant first
    $client2Body = @{ planId = $startPlan.id; displayName = "Chargeback Demo Client 2" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/api/clients/$client2AppId/$tenantId" -Method Put -Body $client2Body -ContentType "application/json" -Headers $authHeaders | Out-Null
    Write-Host "    ✓ Client 2 → Starter plan (tenant: $tenantId)" -ForegroundColor Green

    # If a secondary tenant ID is provided, also register Client 2 for that tenant
    if (-not [string]::IsNullOrWhiteSpace($SecondaryTenantId)) {
        # Provision service principals in the secondary tenant
        Write-Host "  Provisioning service principals in secondary tenant $SecondaryTenantId..." -ForegroundColor Gray
        Write-Host "    ⚠ You must run the following commands while logged into the secondary tenant:" -ForegroundColor DarkYellow
        Write-Host "      az login --tenant $SecondaryTenantId" -ForegroundColor Yellow
        Write-Host "      az ad sp create --id $apiAppId" -ForegroundColor Yellow
        Write-Host "      az ad sp create --id $client2AppId" -ForegroundColor Yellow
        Write-Host "      az login --tenant $tenantId   # switch back" -ForegroundColor Yellow

        $client2SecondaryBody = @{ planId = $startPlan.id; displayName = "Chargeback Demo Client 2 (Secondary Tenant)" } | ConvertTo-Json
        Invoke-RestMethod -Uri "$baseUrl/api/clients/$client2AppId/$SecondaryTenantId" -Method Put -Body $client2SecondaryBody -ContentType "application/json" -Headers $authHeaders | Out-Null
        Write-Host "    ✓ Client 2 → Starter plan (secondary tenant: $SecondaryTenantId)" -ForegroundColor Green
    }

    $deploymentOutput["enterprisePlanId"] = $entPlan.id
    $deploymentOutput["starterPlanId"] = $startPlan.id

    Write-Host "  Phase 9 complete ✓" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "  ✗ Phase 9 failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    You can manually create plans via the dashboard at https://$containerAppUrl" -ForegroundColor DarkYellow
}

# ============================================================================
# Phase 10: Summary Output
# ============================================================================
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
Write-Host "  Phase 10: Deployment Summary" -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

# Export DemoClient environment values and write a reusable env file
$client1SecretForEnv = if ([string]::IsNullOrWhiteSpace($client1Secret)) { "<client-1-secret>" } else { $client1Secret }
$client2SecretForEnv = if ([string]::IsNullOrWhiteSpace($client2Secret)) { "<client-2-secret>" } else { $client2Secret }
$demoClientEnv = [ordered]@{
    "DemoClient__TenantId"                 = $tenantId
    "DemoClient__SecondaryTenantId"        = if ([string]::IsNullOrWhiteSpace($SecondaryTenantId)) { "" } else { $SecondaryTenantId }
    "DemoClient__ApiScope"                 = "api://$apiAppId/.default"
    "DemoClient__ApimBase"                 = "https://$($ApimName).azure-api.net"
    "DemoClient__ApiVersion"               = "2024-02-01"
    "DemoClient__ChargebackBase"           = "https://$containerAppUrl"
    "DemoClient__Clients__0__Name"         = "Chargeback Sample Client"
    "DemoClient__Clients__0__AppId"        = $client1AppId
    "DemoClient__Clients__0__Secret"       = $client1SecretForEnv
    "DemoClient__Clients__0__Plan"         = "Enterprise"
    "DemoClient__Clients__0__DeploymentId" = "gpt-4o"
    "DemoClient__Clients__0__TenantId"     = $tenantId
    "DemoClient__Clients__1__Name"         = "Chargeback Demo Client 2"
    "DemoClient__Clients__1__AppId"        = $client2AppId
    "DemoClient__Clients__1__Secret"       = $client2SecretForEnv
    "DemoClient__Clients__1__Plan"         = "Starter"
    "DemoClient__Clients__1__DeploymentId" = "gpt-4o-mini"
    "DemoClient__Clients__1__TenantId"     = $tenantId
}
foreach ($entry in $demoClientEnv.GetEnumerator()) {
    Set-Item -Path "Env:$($entry.Key)" -Value ([string]$entry.Value)
    $deploymentOutput[$entry.Key] = [string]$entry.Value
}
$demoEnvFile = Join-Path $RepoRoot "demo\.env.local"
$demoEnvLines = @(
    "# Auto-generated by scripts/setup-azure.ps1"
    "# Update deployment IDs if your Azure OpenAI deployment names differ."
) + ($demoClientEnv.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Set-Content -Path $demoEnvFile -Value $demoEnvLines -Encoding UTF8
$deploymentOutput["demoClientEnvFile"] = $demoEnvFile

# Write deployment output file
$outputFile = Join-Path $RepoRoot "deployment-output.json"
$deploymentOutput | ConvertTo-Json -Depth 3 | Set-Content -Path $outputFile -Encoding UTF8
Write-Host "  Deployment output written to: $outputFile" -ForegroundColor Gray
Write-Host ""

Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║   Deployment Complete!                                   ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  ── Azure Resources ──" -ForegroundColor Cyan
Write-Host "  Resource Group:    $ResourceGroupName"
Write-Host "  ACR:               $AcrName"
Write-Host "  APIM:              $ApimName"
Write-Host "  Container App:     $ContainerAppName"
Write-Host "  Container Env:     $ContainerAppEnvName"
Write-Host "  Redis:             $RedisCacheName"
Write-Host "  Cosmos DB:         $CosmosAccountName"
Write-Host "  AI Services:       $AiServiceName"
Write-Host "  Key Vault:         $KeyVaultName"
Write-Host "  Log Analytics:     $LogAnalyticsWorkspaceName"
Write-Host "  App Insights:      $AppInsightsName"
Write-Host "  Storage Account:   $StorageAccountName"
Write-Host ""
Write-Host "  ── URLs ──" -ForegroundColor Cyan
Write-Host "  Dashboard:         https://$containerAppUrl"
Write-Host "  APIM Gateway:      https://$($ApimName).azure-api.net"
if ($deploymentOutput["logAnalyticsWorkbookUrl"]) {
    Write-Host "  Log Analytics WB:  $($deploymentOutput["logAnalyticsWorkbookUrl"])"
}
Write-Host ""
Write-Host "  ── Entra App Registrations ──" -ForegroundColor Cyan
Write-Host "  API App ID:        $apiAppId"
Write-Host "  API Audience:      api://$apiAppId"
Write-Host "  Client 1 App ID:   $client1AppId"
if ($client1Secret) {
    Write-Host "  Client 1 Secret:   $client1Secret" -ForegroundColor DarkYellow
}
Write-Host "  Client 2 App ID:   $client2AppId"
if ($client2Secret) {
    Write-Host "  Client 2 Secret:   $client2Secret" -ForegroundColor DarkYellow
}
Write-Host ""
if ($deploymentOutput["dashboardUiEnvFile"]) {
    Write-Host "  ── Dashboard UI Auth Config ──" -ForegroundColor Cyan
    Write-Host "  Env file:          $($deploymentOutput["dashboardUiEnvFile"])"
    Write-Host "  Contains:          VITE_AZURE_CLIENT_ID, VITE_AZURE_TENANT_ID, VITE_AZURE_SCOPE"
    Write-Host ""
}
Write-Host "  ── DemoClient Config Exports ──" -ForegroundColor Cyan
Write-Host "  Env file:          $demoEnvFile"
Write-Host "  Sample template:   demo\\.env.sample"
Write-Host "  Session vars:      DemoClient__* exported in current PowerShell session"
Write-Host "  Run DemoClient:    dotnet run --project demo"
Write-Host ""
Write-Host "  ── Next Steps ──" -ForegroundColor Cyan
Write-Host "  1. Open the dashboard: https://$containerAppUrl"
Write-Host "  2. Test the APIM endpoint with a Bearer token"
Write-Host "  3. Check APIM policy is applied: Azure Portal → APIM → APIs → azure-openai-api"
Write-Host "  4. Review deployment-output.json for all resource IDs"
Write-Host ""
Write-Host "  ⚠  Client secrets are shown above — save them securely!" -ForegroundColor DarkYellow
Write-Host ""
