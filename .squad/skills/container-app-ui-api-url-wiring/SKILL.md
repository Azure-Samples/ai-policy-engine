# Container App UI-to-API URL Wiring

## Pattern

When deploying a React SPA alongside a .NET API in the same Azure Container App with `azd`, use **relative URLs** for API calls to avoid hardcoding FQDNs.

## Why This Matters

Azure Container Apps FQDNs are assigned AFTER `azd provision` completes. If the UI needs the API URL at build time (e.g., for Vite `import.meta.env.VITE_API_URL`), there is a timing problem:
- **Preprovision** hook runs BEFORE provisioning → CA FQDN unknown
- **UI build** happens inside `dotnet publish` → needs config at that time
- **Postprovision** hook runs AFTER provisioning → too late for build-time config

## Solution: Same-Origin Relative URLs

If the UI is served FROM the same Container App as the API, use relative URLs.

## Files Modified (This Project)

- `scripts/preprovision.ps1` — Added `VITE_API_URL=` to SPA env file generation
- `scripts/preprovision.sh` — Added `VITE_API_URL=` to SPA env file generation
- `src/aipolicyengine-ui/.env.production.local` — Generated file (git-ignored)

## Reference

- Symptom: User reported dashboard timing out on API calls, no inbound requests in container logs
- Root cause: UI had stale hardcoded API URL from previous CA deployment
- Fix: Switched to same-origin relative URLs, regenerated config, redeployed
- Validation: curl confirmed API reachable, UI now calls correct endpoint
