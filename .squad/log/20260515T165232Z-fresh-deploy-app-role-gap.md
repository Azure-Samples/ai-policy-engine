# Session Log — Fresh Deploy App Role Gap

**Date:** 2026-05-15T16:52:32Z

## Issue

HTTP 403 on routing-policies endpoints after successful login on fresh `azd up`. Root cause: deploying user lacked `AIPolicy.Admin` app role (Terraform only assigns to service principals).

## Resolution

1. Granted Zack role immediately via Graph API
2. Updated postprovision scripts to auto-assign deploying user
3. User must logout/login for token refresh

## Validation

Awaiting Zack's confirmation. Will validate via fresh `azd up` after token refresh confirmed.
