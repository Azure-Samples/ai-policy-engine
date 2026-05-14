# Skill: azd + Terraform Auth Alignment Diagnostic

## Problem

When using `azd` with Terraform as the IaC provider, Terraform providers (azurerm, azuread) default to `az` CLI authentication, NOT azd authentication. If the two CLIs are logged into different tenants, Terraform operations fail with cross-tenant auth errors like AADSTS530084 (Conditional Access Policy violation).

## Diagnostic Pattern

### Step 1: Check azd Identity
```bash
azd auth login --check-status
```
Look for: `Logged in to Azure as <user@domain>`

### Step 2: Check az CLI Identity
```bash
az account show
```
Look for: `tenantId` field (e.g., `99e1e9a1-3a8f-4088-ad5d-60be65ecc59a`)

### Step 3: Check azd Environment Tenant
```bash
cat .azure/{env-name}/.env | grep ENTRA_ID_TENANT_ID
```
Or on Windows:
```powershell
Get-Content .azure\{env-name}\.env | Select-String ENTRA_ID_TENANT_ID
```

### Step 4: Verify Alignment
All three must match:
- azd user's tenant (from Step 1)
- az CLI tenantId (from Step 2)
- ENTRA_ID_TENANT_ID from azd env (from Step 3)

## Fix

If misaligned, run:
```bash
az login
```
Then select the same tenant/subscription that azd is using.

Optionally set the subscription explicitly:
```bash
az account set --subscription <subscription-id-from-azd-env>
```

## Why This Matters

- **azd** manages environment variables, orchestrates deployment workflows
- **Terraform providers** use `az` CLI credentials by default (via DefaultAzureCredential)
- If they're on different tenants, Terraform tries to create resources in subscription A (tenant X) using credentials from tenant Y → cross-tenant token request → CAP violation

## Key Learning

When using azd + Terraform:
- Always check auth alignment BEFORE running `azd provision` or `azd up`
- Both azd AND az CLI must be logged into the SAME tenant
- Cross-tenant auth fails even if both identities have access to the target subscription

## When to Apply

Use this diagnostic when you see:
- AADSTS530084 errors during `azd provision`
- "Failed to authenticate" errors from Terraform providers
- "Conditional Access Policy" violations during infrastructure provisioning
- Any cross-tenant auth errors when using azd + Terraform

## Verification

After aligning auth:
```bash
azd provision --preview
```
Should succeed without auth errors. Terraform plan should generate successfully.
