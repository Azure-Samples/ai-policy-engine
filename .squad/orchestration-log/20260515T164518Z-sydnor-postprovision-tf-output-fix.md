# Orchestration Log: Sydnor Postprovision Terraform Output Fix

**Timestamp:** 2026-05-15T16:45:18Z  
**Agent:** Sydnor (Infra/DevOps)  
**Status:** ✅ Complete

## Summary

Sydnor debugged and fixed a critical postprovision regression where fresh `azd up` deploys silently failed to register redirect URIs on the API app registration, causing AADSTS500113 login failures.

**Root Cause:** Postprovision script queried Bicep-era environment variable names (`AZURE_RESOURCE_GROUP`, `COSMOS_ENDPOINT`) instead of actual Terraform output variable names (`resource_group_name`, `cosmos_endpoint`). Variable name mismatch caused silent script failure on fresh deploys.

**Resolution:** Updated `scripts/postprovision.ps1` lines 11–12 to use correct Terraform output variable names. Reran `azd hooks run postprovision` successfully. Verified redirect URI registration on Terraform-managed API app.

## Files Modified

- `scripts/postprovision.ps1` — Fixed resource group + cosmos endpoint variable names

## Verification

- ✅ Fresh deploy portal loads cleanly
- ✅ API endpoint reachable (HTTP 401 auth required)
- ✅ Redirect URI registered on correct app (`4eda37fc-969c-4262-8569-ddcd68aa0370`)
- ✅ `azd hooks run postprovision` completes without errors

## Validation Status

Awaiting Zack's login confirmation in browser before commit.

## Decision(s) Merged

- **2026-05-15 Decision:** Postprovision scripts must use Terraform output variable names (not azd built-in names)

## Cross-Agent Notes

No other agents involved. Fix is isolated to postprovision infra workflow.
