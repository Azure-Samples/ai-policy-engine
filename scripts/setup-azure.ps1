#Requires -Modules Az.Accounts, Az.Resources, Az.ContainerRegistry

<#
.SYNOPSIS
    Deploys the Azure OpenAI AI Policy Environment from scratch.
.DESCRIPTION
    Automates: Resource Group, ACR, Entra App Registrations, Docker build/push,
    Bicep infrastructure, APIM configuration, and initial plan setup.
.PARAMETER Location
    Azure region for all resources (default: eastus2)
.PARAMETER WorkloadName
    Short name used as prefix for all resources (default: aipolicy)
.PARAMETER ResourceGroupName
    Resource group name (default: rg-aipolicy-{Location})
.PARAMETER SkipBicep
    Skip the Bicep deployment (useful when re-running post-deploy steps)
.PARAMETER SkipDocker
    Skip Docker build/push (useful when image is already in ACR)
.PARAMETER EnableJwt
    Deploy the JWT-authenticated OpenAI API endpoint (default: true). Use -EnableJwt:$false to disable.
.PARAMETER EnableKeys
    Deploy the subscription-key-authenticated OpenAI API endpoint (default: true). Use -EnableKeys:$false to disable.
.PARAMETER SecondaryTenantId
    Optional second Entra tenant ID. When provided, Client 2 (multi-tenant) is also
    registered for billing under this tenant — useful for demonstrating per-tenant
    chargeback with a single client app serving multiple organizations.
.EXAMPLE
    .\setup-azure.ps1 -Location eastus2 -WorkloadName aipolicy
.EXAMPLE
    .\setup-azure.ps1 -Location eastus2 -WorkloadName aipolicy -SecondaryTenantId "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
#>
param(
    [string]$Location = "eastus2",
    [string]$WorkloadName = "aipolicy",
    [string]$ResourceGroupName = "",
    [string]$SecondaryTenantId = "",
    [switch]$SkipBicep,
    [switch]$SkipDocker,
    [bool]$EnableJwt = $true,
    [bool]$EnableKeys = $true,
    [bool]$IncludeExternalDemoClient = $true
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
Write-Host "║   Azure OpenAI AI Policy - Full Environment Setup        ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Location:       $Location"
Write-Host "  Workload:       $WorkloadName"
Write-Host "  Resource Group: $ResourceGroupName"
Write-Host ""

# Tracking variables for deployment output
$deploymentOutput = @{}
$script:LastAzError = ""
$script:TenantIdHint = ""

# ----------------------------------------------------------------------------
# Invoke-AzRetry
#
# Drop-in wrapper for the `az` CLI. Captures stdout, retries on transient
# Microsoft Graph / ARM failures (RemoteDisconnected, connection aborted,
# 5xx, 429, timeouts) with exponential backoff. Preserves $LASTEXITCODE and
# emits the original stdout so callers that pipe to ConvertFrom-Json or check
# $LASTEXITCODE continue to work unchanged, and records the last stderr text so
# higher-level helpers can surface targeted remediation.
#
# Usage:   Invoke-AzRetry ad app update --id $apiAppId --identifier-uris "api://$apiAppId"
# ----------------------------------------------------------------------------
function Invoke-AzRetry {
    $maxAttempts = 5
    $transientPattern = 'RemoteDisconnected|Connection aborted|Read timed out|ReadTimeoutError|ServiceUnavailable|BadGateway|GatewayTimeout|Too Many Requests|HTTPSConnectionPool|ConnectionReset|ConnectionError|Temporary failure in name resolution|Max retries exceeded|\(50[0234]\)|\(429\)'
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $global:LASTEXITCODE = 0
        $merged = & az @args 2>&1
        $exitCode = $LASTEXITCODE
        $stdout = @($merged | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        if ($exitCode -eq 0) {
            $script:LastAzError = ""
            return $stdout
        }
        $stderrText = (@($merged | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }) | ForEach-Object { $_.Exception.Message }) -join "`n"
        $script:LastAzError = $stderrText
        if ($attempt -ge $maxAttempts -or $stderrText -notmatch $transientPattern) {
            $global:LASTEXITCODE = $exitCode
            if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                Write-Host $stderrText -ForegroundColor DarkRed
            }
            return $stdout
        }
        $delay = [int][Math]::Min(30, [Math]::Pow(2, $attempt))
        Write-Host "    ⚠ Transient Azure CLI error (attempt $attempt/$maxAttempts). Retrying in ${delay}s..." -ForegroundColor DarkYellow
        Start-Sleep -Seconds $delay
    }
}

function Get-AzFailureMessage {
    param(
        [Parameter(Mandatory = $true)][string]$Operation
    )

    $stderrText = $script:LastAzError
    if ($stderrText -match 'Continuous access evaluation resulted in challenge' -or
        $stderrText -match 'TokenCreatedWithOutdatedPolicies' -or
        $stderrText -match 'InteractionRequired') {
        $loginHint = if ([string]::IsNullOrWhiteSpace($script:TenantIdHint)) {
            "Run 'az login --scope https://graph.microsoft.com//.default' and rerun the script."
        } else {
            "Run 'az login --tenant $($script:TenantIdHint) --scope https://graph.microsoft.com//.default' and rerun the script."
        }

        return "$Operation failed because Microsoft Graph rejected the current Azure CLI session due to a Continuous Access Evaluation challenge (TokenCreatedWithOutdatedPolicies). $loginHint"
    }

    if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
        return "$Operation failed. Azure CLI said: $stderrText"
    }

    return "$Operation failed."
}

function Throw-AzFailure {
    param(
        [Parameter(Mandatory = $true)][string]$Operation
    )

    throw (Get-AzFailureMessage -Operation $Operation)
}

function ConvertFrom-AzJsonOutput {
    param(
        [AllowNull()]$Output,
        [Parameter(Mandatory = $true)][string]$Operation,
        [switch]$AllowEmpty
    )

    if ($LASTEXITCODE -ne 0) {
        Throw-AzFailure -Operation $Operation
    }

    $text = if ($null -eq $Output) {
        ""
    } elseif ($Output -is [System.Array]) {
        ($Output | ForEach-Object { "$_" }) -join "`n"
    } else {
        "$Output"
    }
    $text = $text.Trim()

    if ([string]::IsNullOrWhiteSpace($text)) {
        if ($AllowEmpty) { return $null }
        throw "$Operation returned no JSON output."
    }

    try {
        return $text | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $snippet = if ($text.Length -gt 400) { "$($text.Substring(0, 400))..." } else { $text }
        throw "$Operation returned invalid JSON: $snippet"
    }
}

function Ensure-ServicePrincipal {
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $spId = Invoke-AzRetry ad sp show --id $AppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($spId)) {
        Write-Host "    ✓ Service principal exists for $DisplayName" -ForegroundColor Green
        return
    }

    Write-Host "    Creating service principal for $DisplayName..." -ForegroundColor Gray
    Invoke-AzRetry ad sp create --id $AppId -o none | Out-Null
    if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Creating service principal for $DisplayName ($AppId)" }

    $spReady = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Seconds 3
        $spId = Invoke-AzRetry ad sp show --id $AppId --query "id" -o tsv 2>$null
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
    $existingAccess = Invoke-AzRetry ad app show --id $ClientAppId --query "requiredResourceAccess[?resourceAppId=='$ApiAppId'].resourceAccess[].id" -o tsv 2>$null
    $alreadyHasScope = ($existingAccess -split "`n" | ForEach-Object { $_.Trim() }) -contains $ScopeId

    if (-not $alreadyHasScope) {
        Write-Host "    Adding delegated scope permission for $ClientDisplayName..." -ForegroundColor Gray
        Invoke-AzRetry ad app permission add --id $ClientAppId --api $ApiAppId --api-permissions "$ScopeId=Scope" -o none | Out-Null
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Adding delegated API permission for $ClientDisplayName" }
        Write-Host "    ✓ Delegated scope permission added" -ForegroundColor Green
    } else {
        Write-Host "    ✓ Delegated scope permission already present for $ClientDisplayName" -ForegroundColor Green
    }

    $consentGranted = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Invoke-AzRetry ad app permission admin-consent --id $ClientAppId -o none 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $consentGranted = $true
            break
        }

        Write-Host "    Admin consent attempt $attempt/10 failed for $ClientDisplayName — retrying in 5s..." -ForegroundColor DarkYellow
        Start-Sleep -Seconds 5
    }

    if (-not $consentGranted) {
        if ($LASTEXITCODE -ne 0) {
            Throw-AzFailure -Operation "Granting admin consent for $ClientDisplayName"
        }

        throw "Failed to grant admin consent for $ClientDisplayName after retries."
    }
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
    $script:TenantIdHint = $tenantId
    Write-Host "    ✓ Logged in: $($account.user.name)" -ForegroundColor Green
    Write-Host "    ✓ Subscription: $($account.name) ($subscriptionId)" -ForegroundColor Green
    Write-Host "    ✓ Tenant: $tenantId" -ForegroundColor Green

    Write-Host "  Checking Microsoft Graph access..." -ForegroundColor Gray
    $graphToken = Invoke-AzRetry account get-access-token --resource "https://graph.microsoft.com/" --query "accessToken" -o tsv 2>$null
    $graphTokenText = ($graphToken | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($graphTokenText)) {
        Throw-AzFailure -Operation "Microsoft Graph authentication check"
    }
    Write-Host "    ✓ Microsoft Graph access ready" -ForegroundColor Green

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
    Write-Host "  Creating API app registration 'AI Policy API'..." -ForegroundColor Gray

    # Check if it already exists
    $scopeId = ""
    $existingApiApp = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app list --display-name "AI Policy API" --query "[0]" 2>$null) -Operation "Looking up API app registration 'AI Policy API'" -AllowEmpty
    if ($existingApiApp) {
        $apiAppId = $existingApiApp.appId
        $apiObjId = $existingApiApp.id
        Write-Host "    ✓ Reusing existing API app: $apiAppId" -ForegroundColor Green
    } else {
        $apiApp = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app create --display-name "AI Policy API" --sign-in-audience AzureADMultipleOrgs) -Operation "Creating API app registration 'AI Policy API'"
        $apiAppId = $apiApp.appId
        $apiObjId = $apiApp.id
        Write-Host "    ✓ API app created (multi-tenant): $apiAppId" -ForegroundColor Green

    }

    # Ensure the API app is multi-tenant (required for cross-tenant delegated auth)
    Invoke-AzRetry ad app update --id $apiAppId --sign-in-audience AzureADMultipleOrgs 2>$null | Out-Null

    # Add Microsoft Graph openid permission (required for cross-tenant admin consent)
    $graphOpenIdId = "37f7f235-527c-4136-accd-4a02d197296e"
    Write-Host "  Ensuring Microsoft Graph openid permission on API app..." -ForegroundColor Gray
    $apiGraphAccess = Invoke-AzRetry ad app show --id $apiAppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($apiGraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        Invoke-AzRetry ad app permission add --id $apiAppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null | Out-Null
    }
    Write-Host "    ✓ Graph openid permission configured on API app" -ForegroundColor Green

    # Ensure Application ID URI is set
    Invoke-AzRetry ad app update --id $apiAppId --identifier-uris "api://$apiAppId" | Out-Null
    if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Setting API app identifier URI" }
    Write-Host "    ✓ Identifier URI set: api://$apiAppId" -ForegroundColor Green

    # Resolve existing API scope, or create it if missing
    $scopeId = Invoke-AzRetry ad app show --id $apiAppId --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($scopeId)) {
        $scopeId = [guid]::NewGuid().ToString()
        $scopeBody = @{
            api = @{
                oauth2PermissionScopes = @(@{
                    id                       = $scopeId
                    adminConsentDisplayName  = "Access AI Policy API"
                    adminConsentDescription  = "Allows the app to access the AI Policy API"
                    type                     = "Admin"
                    value                    = "access_as_user"
                    isEnabled                = $true
                })
            }
        } | ConvertTo-Json -Depth 5 -Compress

        # Write to temp file to avoid shell escaping issues
        $scopeFile = Join-Path $env:TEMP "scope-body.json"
        [System.IO.File]::WriteAllText($scopeFile, $scopeBody, [System.Text.UTF8Encoding]::new($false))
        Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$scopeFile" -o none | Out-Null
        Remove-Item $scopeFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Exposing API scope 'access_as_user'" }
        Write-Host "    ✓ API scope 'access_as_user' exposed" -ForegroundColor Green
    } else {
        Write-Host "    ✓ API scope 'access_as_user' already present" -ForegroundColor Green
    }

    Write-Host "  Ensuring API enterprise application exists..." -ForegroundColor Gray
    Ensure-ServicePrincipal -AppId $apiAppId -DisplayName "AI Policy API"

    # Ensure AIPolicy.Export app role exists on the API app
    Write-Host "  Ensuring 'AIPolicy.Export' app role..." -ForegroundColor Gray
    $existingExportRole = Invoke-AzRetry ad app show --id $apiAppId --query "appRoles[?value=='AIPolicy.Export'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingExportRole)) {
        $exportRoleId = [guid]::NewGuid().ToString()
        $currentRoles = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app show --id $apiAppId --query "appRoles" -o json 2>$null) -Operation "Reading API app roles"
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $exportRoleId
            allowedMemberTypes = @("User", "Application")
            displayName        = "AI Policy Export"
            description        = "Allows the user or application to export AI Policy billing summaries and audit trails"
            value              = "AIPolicy.Export"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none | Out-Null
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Adding AIPolicy.Export app role" }
        Write-Host "    ✓ 'AIPolicy.Export' app role created (ID: $exportRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'AIPolicy.Export' app role already exists" -ForegroundColor Green
        $exportRoleId = $existingExportRole

        # Ensure allowedMemberTypes includes User (may have been created as Application-only)
        $currentAllowedTypes = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app show --id $apiAppId --query "appRoles[?value=='AIPolicy.Export'] | [0].allowedMemberTypes" -o json 2>$null) -Operation "Reading AIPolicy.Export app role configuration" -AllowEmpty
        if ($currentAllowedTypes -and ($currentAllowedTypes -notcontains "User")) {
            Write-Host "    Updating app role to allow User assignments..." -ForegroundColor Gray
            $currentRoles = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app show --id $apiAppId --query "appRoles" -o json 2>$null) -Operation "Reading API app roles"
            foreach ($role in $currentRoles) {
                if ($role.value -eq "AIPolicy.Export") {
                    $role.allowedMemberTypes = @("User", "Application")
                }
            }
            $roleBody = @{ appRoles = $currentRoles } | ConvertTo-Json -Depth 5 -Compress
            $roleFile = Join-Path $env:TEMP "app-role-body.json"
            [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
            Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none | Out-Null
            Remove-Item $roleFile -ErrorAction SilentlyContinue
            Write-Host "    ✓ App role updated to allow User + Application" -ForegroundColor Green
        }
    }

    # Ensure AIPolicy.Admin app role exists
    Write-Host "  Ensuring 'AIPolicy.Admin' app role..." -ForegroundColor Gray
    $existingAdminRole = Invoke-AzRetry ad app show --id $apiAppId --query "appRoles[?value=='AIPolicy.Admin'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingAdminRole)) {
        $adminRoleId = [guid]::NewGuid().ToString()
        $currentRoles = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app show --id $apiAppId --query "appRoles" -o json 2>$null) -Operation "Reading API app roles"
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $adminRoleId
            allowedMemberTypes = @("User", "Application")
            displayName        = "AIPolicy Admin"
            description        = "Allows the user or application to manage billing plans, client assignments, pricing, and usage policies"
            value              = "AIPolicy.Admin"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none | Out-Null
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Adding AIPolicy.Admin app role" }
        Write-Host "    ✓ 'AIPolicy.Admin' app role created (ID: $adminRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'AIPolicy.Admin' app role already exists" -ForegroundColor Green
        $adminRoleId = $existingAdminRole
    }

    # Ensure AIPolicy.Apim app role exists (for APIM managed identity → Container App auth)
    Write-Host "  Ensuring 'AIPolicy.Apim' app role..." -ForegroundColor Gray
    $existingApimRole = Invoke-AzRetry ad app show --id $apiAppId --query "appRoles[?value=='AIPolicy.Apim'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingApimRole)) {
        $apimRoleId = [guid]::NewGuid().ToString()
        $currentRoles = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app show --id $apiAppId --query "appRoles" -o json 2>$null) -Operation "Reading API app roles"
        if (-not $currentRoles) { $currentRoles = @() }
        $newRole = @{
            id                 = $apimRoleId
            allowedMemberTypes = @("Application")
            displayName        = "APIM Service"
            description        = "Allows APIM to call the AIPolicy API precheck and log ingest endpoints"
            value              = "AIPolicy.Apim"
            isEnabled          = $true
        }
        $allRoles = @($currentRoles) + @($newRole)
        $roleBody = @{ appRoles = $allRoles } | ConvertTo-Json -Depth 5 -Compress
        $roleFile = Join-Path $env:TEMP "app-role-body.json"
        [System.IO.File]::WriteAllText($roleFile, $roleBody, [System.Text.UTF8Encoding]::new($false))
        Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" --headers "Content-Type=application/json" --body "@$roleFile" -o none | Out-Null
        Remove-Item $roleFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Adding AIPolicy.Apim app role" }
        Write-Host "    ✓ 'AIPolicy.Apim' app role created (ID: $apimRoleId)" -ForegroundColor Green
    } else {
        Write-Host "    ✓ 'AIPolicy.Apim' app role already exists" -ForegroundColor Green
        $apimRoleId = $existingApimRole
    }

    # Assign AIPolicy.Export and AIPolicy.Admin roles to the deploying user
    Write-Host "  Assigning app roles to deploying user..." -ForegroundColor Gray
    $currentUserOid = Invoke-AzRetry ad signed-in-user show --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($currentUserOid)) {
        $apiSpId = Invoke-AzRetry ad sp show --id $apiAppId --query "id" -o tsv 2>$null
        if (-not [string]::IsNullOrWhiteSpace($apiSpId)) {
            foreach ($roleEntry in @(
                @{ Name = "AIPolicy.Export"; Id = $exportRoleId },
                @{ Name = "AIPolicy.Admin";  Id = $adminRoleId }
            )) {
                $existingAssignment = Invoke-AzRetry rest --method GET `
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
                    Invoke-AzRetry rest --method POST `
                        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                        --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null | Out-Null
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

    # --- Gateway App (NEW — APIM JWT audience for client→APIM tokens) ---
    Write-Host "  Creating gateway app 'AIPolicy APIM Gateway'..." -ForegroundColor Gray

    $existingGatewayApp = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app list --display-name "AIPolicy APIM Gateway" --query "[0]" 2>$null) -Operation "Looking up gateway app registration 'AIPolicy APIM Gateway'" -AllowEmpty
    if ($existingGatewayApp) {
        $gatewayAppId = $existingGatewayApp.appId
        $gatewayObjId = $existingGatewayApp.id
        Write-Host "    ✓ Reusing existing gateway app: $gatewayAppId" -ForegroundColor Green
    } else {
        $gatewayApp = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app create --display-name "AIPolicy APIM Gateway" --sign-in-audience AzureADMultipleOrgs) -Operation "Creating gateway app registration 'AIPolicy APIM Gateway'"
        $gatewayAppId = $gatewayApp.appId
        $gatewayObjId = $gatewayApp.id
        Write-Host "    ✓ Gateway app created (multi-tenant): $gatewayAppId" -ForegroundColor Green
    }

    # Ensure multi-tenant (required so external clients in other tenants can consent)
    Invoke-AzRetry ad app update --id $gatewayAppId --sign-in-audience AzureADMultipleOrgs 2>$null | Out-Null

    # Ensure Microsoft Graph openid permission on the gateway app (for third-party tenant consent)
    Write-Host "  Ensuring Microsoft Graph openid permission on gateway app..." -ForegroundColor Gray
    $gwGraphAccess = Invoke-AzRetry ad app show --id $gatewayAppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($gwGraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        Invoke-AzRetry ad app permission add --id $gatewayAppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null | Out-Null
    }
    Write-Host "    ✓ Graph openid permission configured on gateway app" -ForegroundColor Green

    # Identifier URI
    Invoke-AzRetry ad app update --id $gatewayAppId --identifier-uris "api://$gatewayAppId" | Out-Null
    if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Setting gateway app identifier URI" }
    Write-Host "    ✓ Gateway identifier URI set: api://$gatewayAppId" -ForegroundColor Green

    # Expose access_as_user OAuth2 scope on the gateway (this is what APIM validates `aud` against)
    $gatewayScopeId = Invoke-AzRetry ad app show --id $gatewayAppId --query "api.oauth2PermissionScopes[?value=='access_as_user'] | [0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($gatewayScopeId)) {
        $gatewayScopeId = [guid]::NewGuid().ToString()
        $scopeBody = @{
            api = @{
                oauth2PermissionScopes = @(@{
                    id                       = $gatewayScopeId
                    adminConsentDisplayName  = "Access OpenAI via APIM Gateway"
                    adminConsentDescription  = "Allows the app to call Azure OpenAI endpoints through the APIM AI Policy gateway"
                    type                     = "Admin"
                    value                    = "access_as_user"
                    isEnabled                = $true
                })
            }
        } | ConvertTo-Json -Depth 5 -Compress

        $scopeFile = Join-Path $env:TEMP "gateway-scope-body.json"
        [System.IO.File]::WriteAllText($scopeFile, $scopeBody, [System.Text.UTF8Encoding]::new($false))
        Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$gatewayObjId" --headers "Content-Type=application/json" --body "@$scopeFile" -o none | Out-Null
        Remove-Item $scopeFile -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Throw-AzFailure -Operation "Exposing gateway 'access_as_user' scope" }
        Write-Host "    ✓ Gateway scope 'access_as_user' exposed" -ForegroundColor Green
    } else {
        Write-Host "    ✓ Gateway scope 'access_as_user' already present" -ForegroundColor Green
    }

    Write-Host "  Ensuring gateway enterprise application exists..." -ForegroundColor Gray
    Ensure-ServicePrincipal -AppId $gatewayAppId -DisplayName "AIPolicy APIM Gateway"

    $deploymentOutput["gatewayAppId"] = $gatewayAppId
    $deploymentOutput["gatewayObjId"] = $gatewayObjId

    # --- Client App 1 ---
    Write-Host "  Creating client app 'AIPolicy Sample Client'..." -ForegroundColor Gray

    $existingClient1 = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app list --display-name "AIPolicy Sample Client" --query "[0]" 2>$null) -Operation "Looking up client app registration 'AIPolicy Sample Client'" -AllowEmpty
    if ($existingClient1) {
        $client1AppId = $existingClient1.appId
        $client1ObjId = $existingClient1.id
        Write-Host "    ✓ Reusing existing client app 1: $client1AppId" -ForegroundColor Green
    } else {
        $client1 = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app create --display-name "AIPolicy Sample Client" --sign-in-audience AzureADMyOrg) -Operation "Creating client app registration 'AIPolicy Sample Client'"
        $client1AppId = $client1.appId
        $client1ObjId = $client1.id
        Write-Host "    ✓ Client app 1 created: $client1AppId" -ForegroundColor Green
    }

    Ensure-ServicePrincipal -AppId $client1AppId -DisplayName "AIPolicy Sample Client"
    Ensure-DelegatedScopeAndConsent -ClientAppId $client1AppId -ApiAppId $gatewayAppId -ScopeId $gatewayScopeId -ClientDisplayName "AIPolicy Sample Client"

    # Add Microsoft Graph openid permission to Client 1
    $client1GraphAccess = Invoke-AzRetry ad app show --id $client1AppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($client1GraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        Invoke-AzRetry ad app permission add --id $client1AppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null | Out-Null
    }

    # Create client secret for client 1
    $client1Secret = Invoke-AzRetry ad app credential reset --id $client1AppId --display-name "setup-script" --years 1 --query "password" -o tsv 2>$null
    if ($client1Secret) {
        Write-Host "    ✓ Client 1 secret created" -ForegroundColor Green
    }

    # Assign AIPolicy.Admin to client 1 SP (used by Phase 9 for plan seeding)
    $client1SpId = Invoke-AzRetry ad sp show --id $client1AppId --query "id" -o tsv 2>$null
    $apiSpId = Invoke-AzRetry ad sp show --id $apiAppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($client1SpId) -and -not [string]::IsNullOrWhiteSpace($apiSpId)) {
        $existingAdminAssign = Invoke-AzRetry rest --method GET `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
            --query "value[?principalId=='$client1SpId' && appRoleId=='$adminRoleId'] | [0].id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingAdminAssign)) {
            $assignBody = @{ principalId = $client1SpId; resourceId = $apiSpId; appRoleId = $adminRoleId } | ConvertTo-Json -Compress
            $assignFile = Join-Path $env:TEMP "client1-admin-role.json"
            [System.IO.File]::WriteAllText($assignFile, $assignBody, [System.Text.UTF8Encoding]::new($false))
            Invoke-AzRetry rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null | Out-Null
            Remove-Item $assignFile -ErrorAction SilentlyContinue
            Write-Host "    ✓ AIPolicy.Admin role assigned to Client 1 SP" -ForegroundColor Green
        } else {
            Write-Host "    ✓ AIPolicy.Admin role already assigned to Client 1 SP" -ForegroundColor Green
        }
    }

    $deploymentOutput["client1AppId"] = $client1AppId
    $deploymentOutput["client1ObjId"] = $client1ObjId
    $deploymentOutput["client1Secret"] = $client1Secret

    # --- Client App 2 (Multi-tenant — demonstrates per-tenant billing) ---
    # Optional: skip via -IncludeExternalDemoClient:$false
    if (-not $IncludeExternalDemoClient) {
        Write-Host "  ⊘ Skipping Client 2 creation (-IncludeExternalDemoClient:`$false)" -ForegroundColor DarkGray
        $client2AppId = ""
        $client2ObjId = ""
        $client2Secret = ""
        $deploymentOutput["client2AppId"] = ""
        $deploymentOutput["client2ObjId"] = ""
        $deploymentOutput["client2Secret"] = ""
    } else {
    Write-Host "  Creating client app 'AIPolicy Demo Client 2' (multi-tenant)..." -ForegroundColor Gray

    $existingClient2 = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app list --display-name "AIPolicy Demo Client 2" --query "[0]" 2>$null) -Operation "Looking up client app registration 'AIPolicy Demo Client 2'" -AllowEmpty
    if ($existingClient2) {
        $client2AppId = $existingClient2.appId
        $client2ObjId = $existingClient2.id
        Write-Host "    ✓ Reusing existing client app 2: $client2AppId" -ForegroundColor Green
        # Ensure it's multi-tenant
        Invoke-AzRetry ad app update --id $client2AppId --sign-in-audience AzureADMultipleOrgs 2>$null | Out-Null
        Write-Host "    ✓ Client 2 updated to multi-tenant (AzureADMultipleOrgs)" -ForegroundColor Green
    } else {
        $client2 = ConvertFrom-AzJsonOutput -Output (Invoke-AzRetry ad app create --display-name "AIPolicy Demo Client 2" --sign-in-audience AzureADMultipleOrgs) -Operation "Creating client app registration 'AIPolicy Demo Client 2'"
        $client2AppId = $client2.appId
        $client2ObjId = $client2.id
        Write-Host "    ✓ Client app 2 created (multi-tenant): $client2AppId" -ForegroundColor Green
    }

    Ensure-ServicePrincipal -AppId $client2AppId -DisplayName "AIPolicy Demo Client 2"
    Ensure-DelegatedScopeAndConsent -ClientAppId $client2AppId -ApiAppId $gatewayAppId -ScopeId $gatewayScopeId -ClientDisplayName "AIPolicy Demo Client 2"

    # Add Microsoft Graph openid permission to Client 2
    $client2GraphAccess = Invoke-AzRetry ad app show --id $client2AppId --query "requiredResourceAccess[?resourceAppId=='00000003-0000-0000-c000-000000000000'].resourceAccess[].id" -o tsv 2>$null
    if (($client2GraphAccess -split "`n" | ForEach-Object { $_.Trim() }) -notcontains $graphOpenIdId) {
        Invoke-AzRetry ad app permission add --id $client2AppId --api 00000003-0000-0000-c000-000000000000 --api-permissions "$graphOpenIdId=Scope" -o none 2>$null | Out-Null
    }

    # Enable public client flow and add localhost redirect URI for interactive auth (cross-tenant demo)
    Invoke-AzRetry ad app update --id $client2AppId --public-client-redirect-uris "http://localhost:29783" --enable-id-token-issuance true 2>$null | Out-Null
    Write-Host "    ✓ Client 2 public client redirect URI configured (http://localhost:29783)" -ForegroundColor Green

    # Create client secret for client 2
    $client2Secret = Invoke-AzRetry ad app credential reset --id $client2AppId --display-name "setup-script" --years 1 --query "password" -o tsv 2>$null
    if ($client2Secret) {
        Write-Host "    ✓ Client 2 secret created" -ForegroundColor Green
    }

    $deploymentOutput["client2AppId"] = $client2AppId
    $deploymentOutput["client2ObjId"] = $client2ObjId
    $deploymentOutput["client2Secret"] = $client2Secret
    }

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

$imageRepository = "$($AcrName).azurecr.io/aipolicy-api"
$runTag = "run-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$imageTag = if ($SkipDocker) { "${imageRepository}:latest" } else { "${imageRepository}:$runTag" }
$deploymentOutput["containerImage"] = $imageTag

if ($SkipDocker) {
    Write-Host "    ⊘ Docker build/push skipped (-SkipDocker)" -ForegroundColor DarkGray
    Write-Host ""
} else {
    try {
        Write-Host "  Writing dashboard auth config for UI build..." -ForegroundColor Gray
        $uiEnvFile = Join-Path $RepoRoot "src\aipolicyengine-ui\.env.production.local"
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
        Write-Host "  ACR managed identity pull configured — no admin credentials needed." -ForegroundColor Gray

        Write-Host "  Checking soft-deleted resource collisions..." -ForegroundColor Gray
        $deletedApimIds = Invoke-AzRetry apim deletedservice list --query "[?name=='$ApimName'].id" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Failed to query soft-deleted APIM services named '$ApimName'." }
        $deletedApimIds = @($deletedApimIds | ForEach-Object { "$_".Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($deletedApimIds.Count -gt 0) {
            foreach ($deletedApimId in $deletedApimIds) {
                if ($deletedApimId -notmatch '/locations/([^/]+)/deletedservices/') {
                    throw "Could not determine the location for soft-deleted APIM service '$ApimName' from '$deletedApimId'."
                }

                $deletedApimLocation = $Matches[1]
                Write-Host "    Purging soft-deleted APIM '$ApimName' in '$deletedApimLocation'..." -ForegroundColor Gray

                $purgeOutput = & az apim deletedservice purge --service-name $ApimName --location $deletedApimLocation -o none 2>&1
                $purgeExitCode = $LASTEXITCODE
                $purgeErrorText = (@($purgeOutput | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) {
                        $_.Exception.Message
                    } else {
                        "$_"
                    }
                })) -join "`n"

                if ($purgeExitCode -eq 0) {
                    Write-Host "    ✓ Purged APIM soft-delete record in $deletedApimLocation" -ForegroundColor Green
                } elseif ($purgeErrorText -match 'ServiceNotFound|does not exist') {
                    Write-Host "    ✓ APIM soft-delete record already absent in $deletedApimLocation" -ForegroundColor Green
                } else {
                    if (-not [string]::IsNullOrWhiteSpace($purgeErrorText)) {
                        Write-Host $purgeErrorText -ForegroundColor DarkRed
                    }
                    throw "Failed to purge soft-deleted APIM service '$ApimName' in '$deletedApimLocation'."
                }
            }
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
        $bicepParameterArgs = @(
            "location=$Location"
            "workloadName=$WorkloadName"
            "apimInstanceName=$ApimName"
            "keyVaultName=$KeyVaultName"
            "redisCacheName=$RedisCacheName"
            "cosmosAccountName=$CosmosAccountName"
            "logAnalyticsWorkspaceName=$LogAnalyticsWorkspaceName"
            "appInsightsName=$AppInsightsName"
            "storageAccountName=$StorageAccountName"
            "aiServiceName=$AiServiceName"
            "containerAppName=$ContainerAppName"
            "containerAppEnvName=$ContainerAppEnvName"
            "containerImage=$imageTag"
            "acrLoginServer=$($AcrName).azurecr.io"
            "acrName=$AcrName"
            "oaiApiName=azure-openai-api"
            "funcApiName=aipolicy-api"
            "enableJwt=$($EnableJwt.ToString().ToLower())"
            "enableKeys=$($EnableKeys.ToString().ToLower())"
        )
        $bicepSharedArgs = @(
            '--resource-group'
            $ResourceGroupName
            '--template-file'
            "$RepoRoot/infra/bicep/main.bicep"
            '--parameters'
        ) + $bicepParameterArgs + @(
            '--only-show-errors'
            '-o'
            'json'
        )

        Write-Host "  Validating Bicep template..." -ForegroundColor Gray
        $bicepValidationArgs = @('deployment', 'group', 'validate') + $bicepSharedArgs
        $bicepValidationResult = & az @bicepValidationArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            $bicepValidationText = ($bicepValidationResult | Out-String).Trim()
            if ($bicepValidationText -match 'The content for this response was already consumed') {
                Write-Host "    ⚠ Azure CLI validation bug hit: $bicepValidationText" -ForegroundColor DarkYellow
                Write-Host "    Continuing to deployment create so ARM can surface the real error details..." -ForegroundColor DarkYellow
            } else {
                Write-Host "    Bicep validation error details:" -ForegroundColor Red
                Write-Host $bicepValidationText -ForegroundColor DarkRed
                throw "Bicep template validation failed. See error output above."
            }
        } else {
            Write-Host "    ✓ Bicep template validation passed" -ForegroundColor Green
        }

        $bicepDeploymentName = "$WorkloadName-infra-$(Get-Date -Format 'yyyyMMddHHmmss')"
        Write-Host "    Deployment name: $bicepDeploymentName" -ForegroundColor Gray

        $bicepCreateArgs = @('deployment', 'group', 'create', '--name', $bicepDeploymentName) + $bicepSharedArgs
        $bicepResult = & az @bicepCreateArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    Bicep deployment error details:" -ForegroundColor Red
            $bicepErrorText = ($bicepResult | Out-String).Trim()
            if (-not [string]::IsNullOrWhiteSpace($bicepErrorText)) {
                Write-Host $bicepErrorText -ForegroundColor DarkRed
            }

            $armDeploymentError = az deployment group show `
                --resource-group $ResourceGroupName `
                --name $bicepDeploymentName `
                --query "properties.error" -o json 2>$null
            $armDeploymentErrorText = ($armDeploymentError | Out-String).Trim()
            if (-not [string]::IsNullOrWhiteSpace($armDeploymentErrorText) -and $armDeploymentErrorText -ne 'null') {
                Write-Host "    ARM deployment error payload:" -ForegroundColor Red
                Write-Host $armDeploymentErrorText -ForegroundColor DarkRed
            }

            $failedOperations = az deployment operation group list `
                --resource-group $ResourceGroupName `
                --name $bicepDeploymentName `
                --query "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceName,statusMessage:properties.statusMessage}" `
                -o json 2>$null
            $failedOperationsText = ($failedOperations | Out-String).Trim()
            if (-not [string]::IsNullOrWhiteSpace($failedOperationsText) -and $failedOperationsText -ne '[]' -and $failedOperationsText -ne 'null') {
                Write-Host "    Failed deployment operations:" -ForegroundColor Red
                Write-Host $failedOperationsText -ForegroundColor DarkRed
            }

            throw "Bicep deployment failed. See error output above."
        }

        $bicepResultText = ($bicepResult | Out-String).Trim()
        try {
            $bicepDeployment = $bicepResultText | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $jsonStart = $bicepResultText.IndexOf('{')
            $jsonEnd = $bicepResultText.LastIndexOf('}')
            if ($jsonStart -lt 0 -or $jsonEnd -lt $jsonStart) {
                Write-Host "    Unexpected deployment output:" -ForegroundColor Red
                Write-Host $bicepResultText -ForegroundColor DarkRed
                throw "Bicep deployment succeeded but output was not valid JSON."
            }

            $bicepJson = $bicepResultText.Substring($jsonStart, $jsonEnd - $jsonStart + 1)
            $bicepDeployment = $bicepJson | ConvertFrom-Json
        }

        $bicepOutputs = $bicepDeployment.properties.outputs
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
        az containerapp update --name $ContainerAppName --resource-group $ResourceGroupName --set-env-vars "ConnectionStrings__aipolicy=$cosmosEndpoint" -o none
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

    # Assign AIPolicy.Apim app role to APIM managed identity
    Write-Host "  Assigning 'AIPolicy.Apim' app role to APIM managed identity..." -ForegroundColor Gray
    $apiSpId = Invoke-AzRetry ad sp show --id $apiAppId --query "id" -o tsv 2>$null
    if (-not [string]::IsNullOrWhiteSpace($apiSpId) -and -not [string]::IsNullOrWhiteSpace($apimPrincipal)) {
        $apimSpId = Invoke-AzRetry ad sp show --id $apimPrincipal --query "id" -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($apimSpId)) {
            $apimSpId = $apimPrincipal
        }
        $existingApimAssignment = Invoke-AzRetry rest --method GET `
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
            Invoke-AzRetry rest --method POST `
                --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$apiSpId/appRoleAssignedTo" `
                --headers "Content-Type=application/json" --body "@$assignFile" -o none 2>$null | Out-Null
            Remove-Item $assignFile -ErrorAction SilentlyContinue
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    ✓ AIPolicy.Apim role assigned to APIM managed identity" -ForegroundColor Green
            } else {
                Write-Host "    ⚠ Could not assign AIPolicy.Apim role to APIM — assign manually" -ForegroundColor DarkYellow
            }
        } else {
            Write-Host "    ✓ AIPolicy.Apim role already assigned to APIM managed identity" -ForegroundColor Green
        }
    } else {
        Write-Host "    ⚠ Could not resolve service principals — assign AIPolicy.Apim role manually" -ForegroundColor DarkYellow
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
        --named-value-id ExpectedAudience --display-name "ExpectedAudience" --value "api://$gatewayAppId" -o none 2>$null
    Write-Host "    ✓ ExpectedAudience = api://$gatewayAppId" -ForegroundColor Green

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id ContainerAppUrl --display-name "ContainerAppUrl" --value "https://$containerAppUrl" -o none 2>$null
    Write-Host "    ✓ ContainerAppUrl = https://$containerAppUrl" -ForegroundColor Green

    az apim nv create --resource-group $ResourceGroupName --service-name $ApimName `
        --named-value-id ContainerAppAudience --display-name "ContainerAppAudience" --value "api://$apiAppId" -o none 2>$null
    Write-Host "    ✓ ContainerAppAudience = api://$apiAppId" -ForegroundColor Green

    # Disable subscription required on the JWT OpenAI API
    if ($EnableJwt) {
        Write-Host "  Disabling subscription requirement on JWT OpenAI API..." -ForegroundColor Gray
        az apim api update --resource-group $ResourceGroupName --service-name $ApimName `
            --api-id azure-openai-api-jwt --subscription-required false -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to update API subscription setting." }
        Write-Host "    ✓ Subscription requirement disabled" -ForegroundColor Green
    } else {
        Write-Host "    ⊘ JWT API disabled — skipping subscription requirement update" -ForegroundColor DarkGray
    }

    if ($EnableJwt) {
        # Fix JWT API path and backend service URL
        Write-Host "  Configuring JWT-based API path and backend..." -ForegroundColor Gray
        $aiEndpoint = az cognitiveservices account list --resource-group $ResourceGroupName `
            --query "[?kind=='AIServices'].properties.endpoint | [0]" -o tsv
        if ($aiEndpoint) {
            az apim api update --resource-group $ResourceGroupName --service-name $ApimName `
                --api-id azure-openai-api-jwt --set path=jwt/openai --service-url "${aiEndpoint}openai" -o none
            Write-Host "    ✓ API path set to /jwt/openai, backend = ${aiEndpoint}openai" -ForegroundColor Green
        } else {
            Write-Host "    ⚠ No AI endpoint found — skipping API path update" -ForegroundColor DarkYellow
        }

        # Upload APIM policy from entra-jwt-policy.xml
        Write-Host "  Uploading APIM JWT validation policy..." -ForegroundColor Gray
        $policyXml = Get-Content "$RepoRoot/policies/entra-jwt-policy.xml" -Raw
        $body = @{ properties = @{ format = "rawxml"; value = $policyXml } } | ConvertTo-Json -Depth 3 -Compress
        $policyFile = Join-Path $env:TEMP "apim-policy.json"
        $policyResponseFile = Join-Path $env:TEMP "apim-policy-response.xml"
        [System.IO.File]::WriteAllText($policyFile, $body, [System.Text.UTF8Encoding]::new($false))

        $policyUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.ApiManagement/service/$ApimName/apis/azure-openai-api-jwt/policies/policy?api-version=2022-08-01"
        Invoke-AzRetry rest --method PUT --uri $policyUri --headers "Content-Type=application/json" --body "@$policyFile" --output-file $policyResponseFile -o none | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to upload APIM JWT policy." }
        Remove-Item $policyFile -ErrorAction SilentlyContinue
        Remove-Item $policyResponseFile -ErrorAction SilentlyContinue
        Write-Host "    ✓ APIM policy uploaded (entra-jwt-policy.xml)" -ForegroundColor Green
    } else {
        Write-Host "    ⊘ JWT API disabled — skipping JWT policy upload" -ForegroundColor DarkGray
    }

    if ($EnableKeys) {
        # Fix key-based API path and backend service URL
        Write-Host "  Configuring key-based API path and backend..." -ForegroundColor Gray
        $aiEndpoint = az cognitiveservices account list --resource-group $ResourceGroupName `
            --query "[?kind=='AIServices'].properties.endpoint | [0]" -o tsv
        if ($aiEndpoint) {
            az apim api update --resource-group $ResourceGroupName --service-name $ApimName `
                --api-id azure-openai-api-keys --set path=keys/openai --service-url "${aiEndpoint}openai" -o none
            Write-Host "    ✓ API path set to /keys/openai, backend = ${aiEndpoint}openai" -ForegroundColor Green
        } else {
            Write-Host "    ⚠ No AI endpoint found — skipping API path update" -ForegroundColor DarkYellow
        }

        # Upload APIM policy from subscription-key-policy.xml
        Write-Host "  Uploading APIM subscription-key policy..." -ForegroundColor Gray
        $keyPolicyXml = Get-Content "$RepoRoot/policies/subscription-key-policy.xml" -Raw
        $keyBody = @{ properties = @{ format = "rawxml"; value = $keyPolicyXml } } | ConvertTo-Json -Depth 3 -Compress
        $keyPolicyFile = Join-Path $env:TEMP "apim-key-policy.json"
        $keyPolicyResponseFile = Join-Path $env:TEMP "apim-key-policy-response.xml"
        [System.IO.File]::WriteAllText($keyPolicyFile, $keyBody, [System.Text.UTF8Encoding]::new($false))

        $keyPolicyUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.ApiManagement/service/$ApimName/apis/azure-openai-api-keys/policies/policy?api-version=2022-08-01"
        Invoke-AzRetry rest --method PUT --uri $keyPolicyUri --headers "Content-Type=application/json" --body "@$keyPolicyFile" --output-file $keyPolicyResponseFile -o none | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to upload subscription-key APIM policy." }
        Remove-Item $keyPolicyFile -ErrorAction SilentlyContinue
        Remove-Item $keyPolicyResponseFile -ErrorAction SilentlyContinue
        Write-Host "    ✓ APIM policy uploaded (subscription-key-policy.xml)" -ForegroundColor Green
    } else {
        Write-Host "    ⊘ Key-based API disabled — skipping subscription-key policy upload" -ForegroundColor DarkGray
    }

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
    # The frontend SPA (MSAL) uses the API app's client ID and sends
    # window.location.origin as the redirect URI (no trailing slash).
    # Entra ID performs exact matching, so URIs must not have trailing slashes.
    $spaRedirectUris = @(
        "https://$containerAppUrl"
        "http://localhost:5173"
    )
    $redirectBody = @{ spa = @{ redirectUris = $spaRedirectUris } } | ConvertTo-Json -Depth 3 -Compress
    $redirectFile = Join-Path $env:TEMP "redirect-body.json"
    [System.IO.File]::WriteAllText($redirectFile, $redirectBody, [System.Text.UTF8Encoding]::new($false))

    # API app — this is the app the dashboard SPA uses as its MSAL clientId
    Write-Host "  Setting SPA redirect URIs on API app (used by dashboard UI)..." -ForegroundColor Gray
    Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$apiObjId" `
        --headers "Content-Type=application/json" --body "@$redirectFile" -o none | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set redirect URIs on API app." }
    Write-Host "    ✓ API app redirect URIs: https://$containerAppUrl, http://localhost:5173" -ForegroundColor Green

    # Client app 1 — also needs the redirect for delegated auth flows
    Write-Host "  Setting SPA redirect URIs on client app 1..." -ForegroundColor Gray
    Invoke-AzRetry rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$client1ObjId" `
        --headers "Content-Type=application/json" --body "@$redirectFile" -o none | Out-Null
    Remove-Item $redirectFile -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { throw "Failed to set redirect URIs on client app 1." }
    Write-Host "    ✓ Client app 1 redirect URIs: https://$containerAppUrl, http://localhost:5173" -ForegroundColor Green

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

    # Acquire an access token using client1 credentials (has AIPolicy.Admin role)
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
    $client1Body = @{ planId = $entPlan.id; displayName = "AIPolicy Sample Client" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/api/clients/$client1AppId/$tenantId" -Method Put -Body $client1Body -ContentType "application/json" -Headers $authHeaders | Out-Null
    Write-Host "    ✓ Client 1 → Enterprise plan (tenant: $tenantId)" -ForegroundColor Green

    # Client 2 is multi-tenant — register with the deployment tenant first (optional)
    if ($IncludeExternalDemoClient -and -not [string]::IsNullOrWhiteSpace($client2AppId)) {
        $client2Body = @{ planId = $startPlan.id; displayName = "AIPolicy Demo Client 2" } | ConvertTo-Json
        Invoke-RestMethod -Uri "$baseUrl/api/clients/$client2AppId/$tenantId" -Method Put -Body $client2Body -ContentType "application/json" -Headers $authHeaders | Out-Null
        Write-Host "    ✓ Client 2 → Starter plan (tenant: $tenantId)" -ForegroundColor Green

        # If a secondary tenant ID is provided, also register Client 2 for that tenant
        if (-not [string]::IsNullOrWhiteSpace($SecondaryTenantId)) {
            # Provision service principals in the secondary tenant
            Write-Host "  Provisioning service principals in secondary tenant $SecondaryTenantId..." -ForegroundColor Gray
            Write-Host "    ⚠ You must run the following commands while logged into the secondary tenant:" -ForegroundColor DarkYellow
            Write-Host "      az login --tenant $SecondaryTenantId" -ForegroundColor Yellow
            Write-Host "      az ad sp create --id $apiAppId" -ForegroundColor Yellow
            Write-Host "      az ad sp create --id $gatewayAppId" -ForegroundColor Yellow
            Write-Host "      az ad sp create --id $client2AppId" -ForegroundColor Yellow
            Write-Host "      az login --tenant $tenantId   # switch back" -ForegroundColor Yellow

            $client2SecondaryBody = @{ planId = $startPlan.id; displayName = "AIPolicy Demo Client 2 (Secondary Tenant)" } | ConvertTo-Json
            Invoke-RestMethod -Uri "$baseUrl/api/clients/$client2AppId/$SecondaryTenantId" -Method Put -Body $client2SecondaryBody -ContentType "application/json" -Headers $authHeaders | Out-Null
            Write-Host "    ✓ Client 2 → Starter plan (secondary tenant: $SecondaryTenantId)" -ForegroundColor Green
        }
    } else {
        Write-Host "    ⊘ Skipping Client 2 plan registration (-IncludeExternalDemoClient:`$false)" -ForegroundColor DarkGray
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
    "DemoClient__ApiScope"                 = "api://$gatewayAppId/.default"
    "DemoClient__ApimBase"                 = "https://$($ApimName).azure-api.net"
    "DemoClient__ApiVersion"               = "2024-02-01"
    "DemoClient__AIPolicyBase"             = "https://$containerAppUrl"
    "DemoClient__Clients__0__Name"         = "AIPolicy Sample Client"
    "DemoClient__Clients__0__AppId"        = $client1AppId
    "DemoClient__Clients__0__Secret"       = $client1SecretForEnv
    "DemoClient__Clients__0__Plan"         = "Enterprise"
    "DemoClient__Clients__0__DeploymentId" = "gpt-4o"
    "DemoClient__Clients__0__TenantId"     = $tenantId
}
if ($IncludeExternalDemoClient -and -not [string]::IsNullOrWhiteSpace($client2AppId)) {
    $demoClientEnv["DemoClient__Clients__1__Name"]         = "AIPolicy Demo Client 2"
    $demoClientEnv["DemoClient__Clients__1__AppId"]        = $client2AppId
    $demoClientEnv["DemoClient__Clients__1__Secret"]       = $client2SecretForEnv
    $demoClientEnv["DemoClient__Clients__1__Plan"]         = "Starter"
    $demoClientEnv["DemoClient__Clients__1__DeploymentId"] = "gpt-4o-mini"
    $demoClientEnv["DemoClient__Clients__1__TenantId"]     = $tenantId
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
Write-Host "  API Audience:      api://$apiAppId   (dashboard UI → Container App, plan seeding)"
Write-Host "  Gateway App ID:    $gatewayAppId"
Write-Host "  Gateway Audience:  api://$gatewayAppId   (client → APIM — used by demo DemoClient__ApiScope)"
Write-Host "  Client 1 App ID:   $client1AppId"
if ($client1Secret) {
    Write-Host "  Client 1 Secret:   $client1Secret" -ForegroundColor DarkYellow
}
if ($IncludeExternalDemoClient -and -not [string]::IsNullOrWhiteSpace($client2AppId)) {
    Write-Host "  Client 2 App ID:   $client2AppId"
    if ($client2Secret) {
        Write-Host "  Client 2 Secret:   $client2Secret" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  Client 2:          (skipped — -IncludeExternalDemoClient:`$false)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "  ⚠ Token audience changed: clients going through APIM must request" -ForegroundColor DarkYellow
Write-Host "    api://$gatewayAppId/.default. Cached tokens targeting the old API" -ForegroundColor DarkYellow
Write-Host "    audience will receive 401 from APIM until refreshed." -ForegroundColor DarkYellow
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
