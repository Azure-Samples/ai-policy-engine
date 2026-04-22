<#
.SYNOPSIS
    Registers service principals for the AI Policy API and Client 2 in a secondary Entra tenant.
.DESCRIPTION
    Multi-tenant Entra apps need their service principals provisioned in each tenant
    they'll authenticate against. This script creates the SPs for the API and Client 2
    in the specified secondary tenant.

    Prerequisites:
    - Azure CLI installed and logged in
    - Admin permissions in the secondary tenant
    - The API app and Client 2 must already be registered as multi-tenant (AzureADMultipleOrgs)
.PARAMETER SecondaryTenantId
    The Entra tenant ID where service principals should be created.
.PARAMETER ApiAppId
    Application (client) ID of the AI Policy API app registration.
.PARAMETER Client2AppId
    Application (client) ID of the AI Policy Demo Client 2.
.EXAMPLE
    .\register-secondary-tenant.ps1 -SecondaryTenantId "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" `
        -ApiAppId "6d82b966-31bf-44c5-89ef-19410e155750" `
        -Client2AppId "94830ce7-8d28-4fc2-868e-20c5f3107fd5"
#>
param(
    [Parameter(Mandatory = $true)][string]$SecondaryTenantId,
    [Parameter(Mandatory = $true)][string]$ApiAppId,
    [Parameter(Mandatory = $true)][string]$Client2AppId
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Register Service Principals in Secondary Tenant        ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Secondary Tenant: $SecondaryTenantId"
Write-Host "  API App ID:       $ApiAppId"
Write-Host "  Client 2 App ID:  $Client2AppId"
Write-Host ""

# Check current login
$currentAccount = az account show 2>&1 | ConvertFrom-Json
$currentTenant = $currentAccount.tenantId

if ($currentTenant -ne $SecondaryTenantId) {
    Write-Host "  Logging into secondary tenant..." -ForegroundColor Gray
    az login --tenant $SecondaryTenantId --allow-no-subscriptions | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to login to tenant $SecondaryTenantId" }
    Write-Host "    ✓ Logged into tenant $SecondaryTenantId" -ForegroundColor Green
} else {
    Write-Host "    ✓ Already logged into secondary tenant" -ForegroundColor Green
}

# Create API app service principal
Write-Host "  Creating API app service principal..." -ForegroundColor Gray
$existingApiSp = az ad sp show --id $ApiAppId 2>$null | ConvertFrom-Json
if ($existingApiSp) {
    Write-Host "    ✓ API app SP already exists: $($existingApiSp.id)" -ForegroundColor Green
} else {
    $apiSp = az ad sp create --id $ApiAppId 2>&1 | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Failed to create API app SP" }
    Write-Host "    ✓ API app SP created: $($apiSp.id)" -ForegroundColor Green
}

# Create Client 2 service principal
Write-Host "  Creating Client 2 service principal..." -ForegroundColor Gray
$existingClient2Sp = az ad sp show --id $Client2AppId 2>$null | ConvertFrom-Json
if ($existingClient2Sp) {
    Write-Host "    ✓ Client 2 SP already exists: $($existingClient2Sp.id)" -ForegroundColor Green
} else {
    $client2Sp = az ad sp create --id $Client2AppId 2>&1 | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Failed to create Client 2 SP" }
    Write-Host "    ✓ Client 2 SP created: $($client2Sp.id)" -ForegroundColor Green
}

Write-Host ""
Write-Host "  ✓ Service principals registered in tenant $SecondaryTenantId" -ForegroundColor Green
Write-Host ""

# Switch back to primary tenant if we changed
if ($currentTenant -ne $SecondaryTenantId) {
    Write-Host "  Switching back to primary tenant $currentTenant..." -ForegroundColor Gray
    az login --tenant $currentTenant | Out-Null
    Write-Host "    ✓ Back to primary tenant" -ForegroundColor Green
}

Write-Host ""
Write-Host "  Done! You can now run the demo with -SecondaryTenantId $SecondaryTenantId" -ForegroundColor Cyan
Write-Host ""
