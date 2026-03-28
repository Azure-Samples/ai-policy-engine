#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seeds default plans and client assignments into the Chargeback API.
    Can be run after Terraform apply or standalone.

.DESCRIPTION
    Creates Enterprise and Starter plans, then assigns sample clients to them.
    Uses the Container App's managed identity via the APIM gateway, or directly
    if run with a valid bearer token. Idempotent — updates existing plans/clients.

.PARAMETER BaseUrl
    Base URL of the Chargeback API (e.g. https://chrgbk-ca.xxx.azurecontainerapps.io).
    If not provided, reads from terraform output.

.PARAMETER Client1AppId
    App (client) ID of the Sample Client. If not provided, reads from terraform output.

.PARAMETER Client2AppId
    App (client) ID of Demo Client 2. If not provided, reads from terraform output.

.PARAMETER TenantId
    Primary tenant ID. If not provided, reads from terraform output.

.PARAMETER SecondaryTenantId
    Optional secondary tenant ID for multi-tenant demo.
#>
param(
    [string]$BaseUrl,
    [string]$Client1AppId,
    [string]$Client2AppId,
    [string]$TenantId,
    [string]$SecondaryTenantId,
    [string]$ApiAppId
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$tfDir = Join-Path $scriptDir "..\infra\terraform"

# Read from Terraform outputs if not provided
if (-not $BaseUrl -or -not $Client1AppId -or -not $Client2AppId -or -not $TenantId -or -not $ApiAppId) {
    Write-Host "Reading Terraform outputs..." -ForegroundColor Gray
    Push-Location $tfDir
    try {
        $tfOutput = terraform output -json | ConvertFrom-Json
        if (-not $BaseUrl) { $BaseUrl = $tfOutput.container_app_url.value }
        if (-not $ApiAppId) { $ApiAppId = $tfOutput.api_app_id.value }
        if (-not $Client1AppId) { $Client1AppId = $tfOutput.client1_app_id.value }
        if (-not $Client2AppId) { $Client2AppId = $tfOutput.client2_app_id.value }
        if (-not $TenantId) { $TenantId = $tfOutput.tenant_id.value }
        if (-not $SecondaryTenantId) { $SecondaryTenantId = $tfOutput.secondary_tenant_id.value }
    }
    finally { Pop-Location }
}

$BaseUrl = $BaseUrl.TrimEnd('/')
Write-Host "Seeding data to: $BaseUrl" -ForegroundColor Cyan

# Acquire a token using the Sample Client's credentials (it has the Admin role).
# Read the client secret from Terraform output.
Write-Host "Acquiring access token via client credentials..." -ForegroundColor Gray
$token = $null

Push-Location $tfDir
try {
    $client1Secret = (terraform output -raw client1_secret 2>$null)
} finally { Pop-Location }

if ($Client1AppId -and $client1Secret -and $TenantId) {
    $body = @{
        grant_type    = "client_credentials"
        client_id     = $Client1AppId
        client_secret = $client1Secret
        scope         = "api://$ApiAppId/.default"
    }
    try {
        $tokenResponse = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body $body -ContentType "application/x-www-form-urlencoded"
        $token = $tokenResponse.access_token
    } catch {
        Write-Host "  Client credentials flow failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# Fallback: try az cli token
if (-not $token) {
    Write-Host "  Falling back to az cli token..." -ForegroundColor DarkYellow
    $token = (az account get-access-token --resource "api://$ApiAppId" --query "accessToken" -o tsv 2>$null)
}

if (-not $token) {
    throw "Could not acquire a token. Ensure Terraform has been applied and you are logged into the correct tenant with 'az login --tenant $TenantId'."
}
Write-Host "  ✓ Token acquired" -ForegroundColor Green

$authHeaders = @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" }

# Wait for Container App to be ready (health check is unauthenticated)
Write-Host "Waiting for Container App to be ready..." -ForegroundColor Gray
$maxRetries = 12
$retryCount = 0
$ready = $false
while (-not $ready -and $retryCount -lt $maxRetries) {
    try {
        Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -TimeoutSec 10 -ErrorAction Stop | Out-Null
        $ready = $true
    }
    catch {
        # Fall back to trying the plans endpoint (anonymous GET)
        try {
            Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Get -TimeoutSec 10 -ErrorAction Stop | Out-Null
            $ready = $true
        } catch {
            $retryCount++
            Write-Host "  Attempt $retryCount/$maxRetries — waiting 10s..." -ForegroundColor DarkGray
            Start-Sleep -Seconds 10
        }
    }
}
if (-not $ready) { throw "Container App not responding after $maxRetries attempts." }
Write-Host "  ✓ Container App is ready" -ForegroundColor Green

# Fetch existing plans
$plansResponse = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Get -Headers $authHeaders -TimeoutSec 15
$existingPlans = @($plansResponse.plans)

# Ensure Enterprise plan
Write-Host "Ensuring Enterprise plan..." -ForegroundColor Gray
$entPlanBody = @{
    name                   = "Enterprise"
    monthlyRate            = 999.99
    monthlyTokenQuota      = 10000000
    tokensPerMinuteLimit   = 200000
    requestsPerMinuteLimit = 120
    allowOverbilling       = $true
    costPerMillionTokens   = 10.0
} | ConvertTo-Json

$enterprisePlan = @($existingPlans | Where-Object { $_.name -and $_.name.Trim() -ieq "Enterprise" }) | Select-Object -First 1
if ($enterprisePlan) {
    $entPlan = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$($enterprisePlan.id)" -Method Put -Body $entPlanBody -ContentType "application/json" -Headers $authHeaders
    Write-Host "  ✓ Enterprise plan updated (ID: $($entPlan.id))" -ForegroundColor Green
}
else {
    $entPlan = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Post -Body $entPlanBody -ContentType "application/json" -Headers $authHeaders
    Write-Host "  ✓ Enterprise plan created (ID: $($entPlan.id))" -ForegroundColor Green
}

# Ensure Starter plan
Write-Host "Ensuring Starter plan..." -ForegroundColor Gray
$startPlanBody = @{
    name                   = "Starter"
    monthlyRate            = 49.99
    monthlyTokenQuota      = 500
    tokensPerMinuteLimit   = 1000
    requestsPerMinuteLimit = 10
    allowOverbilling       = $false
    costPerMillionTokens   = 0
} | ConvertTo-Json

$starterPlan = @($existingPlans | Where-Object { $_.name -and $_.name.Trim() -ieq "Starter" }) | Select-Object -First 1
if ($starterPlan) {
    $startPlan = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$($starterPlan.id)" -Method Put -Body $startPlanBody -ContentType "application/json" -Headers $authHeaders
    Write-Host "  ✓ Starter plan updated (ID: $($startPlan.id))" -ForegroundColor Green
}
else {
    $startPlan = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Post -Body $startPlanBody -ContentType "application/json" -Headers $authHeaders
    Write-Host "  ✓ Starter plan created (ID: $($startPlan.id))" -ForegroundColor Green
}

# Assign clients to plans
Write-Host "Assigning clients to plans..." -ForegroundColor Gray

$client1Body = @{ planId = $entPlan.id; displayName = "Chargeback Sample Client" } | ConvertTo-Json
Invoke-RestMethod -Uri "$BaseUrl/api/clients/$Client1AppId/$TenantId" -Method Put -Body $client1Body -ContentType "application/json" -Headers $authHeaders | Out-Null
Write-Host "  ✓ Client 1 → Enterprise plan (tenant: $TenantId)" -ForegroundColor Green

$client2Body = @{ planId = $startPlan.id; displayName = "Chargeback Demo Client 2" } | ConvertTo-Json
Invoke-RestMethod -Uri "$BaseUrl/api/clients/$Client2AppId/$TenantId" -Method Put -Body $client2Body -ContentType "application/json" -Headers $authHeaders | Out-Null
Write-Host "  ✓ Client 2 → Starter plan (tenant: $TenantId)" -ForegroundColor Green

# Optional secondary tenant
if (-not [string]::IsNullOrWhiteSpace($SecondaryTenantId)) {
    $client2SecondaryBody = @{ planId = $startPlan.id; displayName = "Chargeback Demo Client 2 (Secondary Tenant)" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$BaseUrl/api/clients/$Client2AppId/$SecondaryTenantId" -Method Put -Body $client2SecondaryBody -ContentType "application/json" -Headers $authHeaders | Out-Null
    Write-Host "  ✓ Client 2 → Starter plan (secondary tenant: $SecondaryTenantId)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Seed data complete ✓" -ForegroundColor Green
Write-Host "  Enterprise plan ID: $($entPlan.id)"
Write-Host "  Starter plan ID:    $($startPlan.id)"
