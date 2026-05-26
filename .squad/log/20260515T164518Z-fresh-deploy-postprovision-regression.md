# Session Log: Fresh Deploy Postprovision Regression

**Timestamp:** 2026-05-15T16:45:18Z  
**Session Phase:** Validation + Bug Fix  
**Agent Lead:** Sydnor

## Issue

Fresh `azd up` deploy succeeded but portal login failed (AADSTS500113). Root cause: postprovision script used stale Bicep-era variable names instead of Terraform outputs.

## Fix

Updated `scripts/postprovision.ps1` to use correct Terraform output variable names. Reran postprovision hook. Redirect URI now registered on Terraform-managed API app.

## Outcome

✅ Portal operational. Redirect URI verified. Awaiting user login test.
