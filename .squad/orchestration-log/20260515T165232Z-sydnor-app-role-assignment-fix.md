# Orchestration Log — Sydnor App Role Assignment Fix

**Date:** 2026-05-15T16:52:32Z  
**Agent:** Sydnor (Infra/DevOps)  
**Batch:** Fresh-deploy app role gap remediation

## Summary

Sydnor diagnosed HTTP 403 errors on routing-policies endpoints after fresh `azd up` deployment. Root cause: `AIPolicy.Admin` app role was assigned by Terraform only to service principals, not to human users. Fixed via two complementary approaches:

1. **Immediate:** Granted Zack the role manually via Graph API (`az rest` POST to appRoleAssignedTo)
2. **Durable:** Updated `scripts/postprovision.ps1` and `scripts/postprovision.sh` to auto-assign deploying user to `AIPolicy.Admin` app role, making the portal usable immediately after `azd up`

## Implementation Details

- **Postprovision Pattern:** Query signed-in user via `az ad signed-in-user show`, idempotently assign via Graph API
- **Fail-Safe:** Script continues if assignment fails; user can assign manually later
- **Token Warning:** User must log out/login to refresh token and receive role claims
- **Files Modified:**
  - `scripts/postprovision.ps1` (lines 133+)
  - `scripts/postprovision.sh` (lines 158+)

## Status

- ✅ Manual role assignment succeeded
- ✅ Postprovision scripts updated
- ⚠️ Awaiting Zack's logout/login validation before commit

## Decision Document

See `.squad/decisions/inbox/sydnor-app-role-assignment-postprovision.md` (merged to decisions.md)

## Next Steps

1. Zack validates token refresh (logout/login) and confirms portal access
2. Commit postprovision scripts
3. Validate via fresh `azd up` in test tenant
