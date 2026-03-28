# Query deployed client assignments to diagnose duplicates
$envFile = Join-Path $PSScriptRoot "..\demo\.env.local"
if (-not (Test-Path $envFile)) { Write-Error "demo/.env.local not found"; exit 1 }

Get-Content $envFile | ForEach-Object {
    if ($_ -match '^([^#][^=]+)=(.*)$') {
        Set-Variable -Name $matches[1].Replace("__", "_") -Value $matches[2] -Scope Script
    }
}

# Re-read raw values
$vars = @{}
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^([^#][^=]+)=(.*)$') { $vars[$matches[1]] = $matches[2] }
}

$tenantId = $vars["DemoClient__TenantId"]
$apiScope = $vars["DemoClient__ApiScope"]
$clientId = $vars["DemoClient__Clients__0__AppId"]
$clientSecret = $vars["DemoClient__Clients__0__Secret"]
$baseUrl = $vars["DemoClient__ChargebackBase"]

Write-Host "Acquiring token..." -ForegroundColor Gray
$tokenResponse = Invoke-RestMethod `
    -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" `
    -Method Post `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        grant_type    = "client_credentials"
        client_id     = $clientId
        client_secret = $clientSecret
        scope         = $apiScope
    }

$token = $tokenResponse.access_token
$headers = @{ Authorization = "Bearer $token" }

Write-Host ""
Write-Host "=== Client Assignments ===" -ForegroundColor Cyan
$response = Invoke-RestMethod -Uri "$baseUrl/api/clients" -Headers $headers
$response.clients | ForEach-Object {
    [PSCustomObject]@{
        ClientAppId = $_.clientAppId
        TenantId    = $_.tenantId
        DisplayName = $_.displayName
        Usage       = $_.currentPeriodUsage
        PlanId      = $_.planId
    }
} | Format-Table -AutoSize

Write-Host "=== Usage Summary (Cosmos) ===" -ForegroundColor Cyan
$usage = Invoke-RestMethod -Uri "$baseUrl/api/usage" -Headers $headers
$usage.usageSummaries | ForEach-Object {
    [PSCustomObject]@{
        ClientAppId  = $_.clientAppId
        TenantId     = $_.tenantId
        DeploymentId = $_.deploymentId
        TotalTokens  = $_.totalTokens
        CostToUs     = $_.costToUs
    }
} | Format-Table -AutoSize
