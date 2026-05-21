# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Terraform (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `infra/terraform/` — Terraform modules (core workspace)
- `policies/` — APIM policy definitions (base + DLP variants)
- `src/Chargeback.AppHost/` — Aspire orchestration
- `src/aipolicyengine-ui/` — React frontend (SPA)
- `scripts/` — Deployment and postprovision utilities

## Core Context

**Project Status: Phase 3 + Infrastructure Complete**

All backend phases complete and tested (235+ tests passing):
- **Phase 0:** CosmosDB source-of-truth + Redis cache architecture
- **Phase 1:** Model routing with 7 deployment endpoints + multiplier pricing
- **Phase 2:** Agent365 Observability SDK integration + Purview DLP policy variants
- **Phase 3:** APIM auto-router policies for subscription-key and entra-jwt auth

**Infrastructure: Terraform + azd Complete**
- 77 Azure resources provisioned via `azd up` (9m59s runtime)
- Terraform provider configured in `azure.yaml` with variable substitution template (`main.tfvars.json`)
- Authentication aligned: azd + az CLI on same tenant (99e1e9a1-3a8f-4088-ad5d-60be65ecc59a)
- All services operational: Container App API, APIM gateway, Cosmos DB, Redis Enterprise, Key Vault, Log Analytics
- App IDs registered via Terraform (api_app_id: d5bd33f4-09b1-4602-af88-29c5ec7728e0)

**Current Issue (2026-05-14): AADSTS500113 — Reply URL Mismatch**
- **Problem:** UI auth fails because redirect URIs were registered on legacy app, not Terraform-managed app
- **Fix Applied:** Updated `postprovision.ps1` and `postprovision.sh` to register redirect URIs on correct app (api_app_id)
- **Status:** Redirect URI verified on correct app; awaiting user login confirmation


## Active Learnings

### 2026-05-14 — Redirect URI Registration: Terraform-Managed App vs Legacy App

**Context:** User (Zack) reported AADSTS500113 error when logging into the dashboard: "No reply address is registered for the application."

**Root Cause:**
- The repository has TWO app registrations for the API:
  1. **Terraform-managed** (`api_app_id` = d5bd33f4-09b1-4602-af88-29c5ec7728e0) — "AI Policy API"
  2. **Legacy/manual** (`CONTAINER_APP_CLIENT_ID` = 625db56c-f5cc-4ee5-954d-6775c709055e) — "AI Policy Engine API (ai-policy-engine-k8m2)"
- The UI (src/aipolicyengine-ui) is configured via .env.production.local to use the Terraform-managed app (`VITE_AZURE_CLIENT_ID=d5bd33f4...`)
- But the postprovision scripts were setting redirect URIs on the LEGACY app (CONTAINER_APP_CLIENT_ID), not the Terraform-managed app
- Result: UI tries to auth against the Terraform-managed app, which has no redirect URIs → AADSTS500113

**Fix Applied:**
- Updated both `scripts/postprovision.ps1` and `scripts/postprovision.sh` to use `api_app_id` (Terraform-managed app) instead of `CONTAINER_APP_CLIENT_ID`
- Also fixed error code reference: AADSTS50011 → AADSTS500113 (correct error for "no reply address")
- The postprovision scripts query the ACTUAL Container App FQDN from Azure (not from Terraform state) and register it as a SPA redirect URI
- This is idempotent — re-running doesn't duplicate URIs

**Verification:**
- Ran `azd hooks run postprovision` successfully
- Confirmed redirect URI `https://ca-h75aielsaei6q.proudsky-ba978644.eastus2.azurecontainerapps.io` is now registered on the Terraform-managed API app (d5bd33f4...)
- The UI's MSAL config uses `redirectUri: window.location.origin`, so when the UI is served from the Container App, the redirect flow will work

**Key Learning:**
- When troubleshooting AADSTS errors, always verify WHICH app registration the client is actually using (check VITE_AZURE_CLIENT_ID, not assumptions)
- The postprovision script pattern: query live Azure resources (not Terraform state) because Terraform state can be out of sync
- Use `az ad app show --id <appId> --query "{displayName:displayName,spa:spa.redirectUris}"` to quickly verify redirect URI registration

**Files Modified:**
- scripts/postprovision.ps1 — Changed `CONTAINER_APP_CLIENT_ID` → `api_app_id`
- scripts/postprovision.sh — Changed `CONTAINER_APP_CLIENT_ID` → `api_app_id`

**Status:** ✅ FIXED. Awaiting user confirmation that login now works.

### 2026-05-14 — UI-to-API URL Wiring: Same-Origin Pattern for Container Apps

**Context:** User (Zack) reported that the dashboard was timing out on all API calls. Container logs showed the API was running cleanly but receiving NO inbound requests at all.

**Root Cause:**
- The UI was trying to call a stale API URL: `https://ai-policy-engine-k8m2-ca.ambitioussky-30956417.eastus2.azurecontainerapps.io`
- This URL no longer exists (DNS resolves but connection hangs — stale/decommissioned CA)
- The current, working Container App is: `https://ca-h75aielsaei6q.proudsky-ba978644.eastus2.azurecontainerapps.io`
- The `.env.production.local` file (which sets `VITE_API_URL` at build time) had the old URL hardcoded
- The `preprovision.ps1` script (called by `azd hooks run preprovision`) was NOT writing `VITE_API_URL` at all — only auth variables
- Result: Every UI rebuild used whatever stale value was in `.env.production.local`

**Why This Happened:**
- The Container App FQDN is NOT known at preprovision time (it's assigned during `azd provision`)
- The UI is built inside `dotnet publish`, which runs AFTER provisioning, but BEFORE we know the final CA URL
- There's a separate `deploy-container.ps1` script that writes `VITE_API_URL` with the correct FQDN, but it's NOT part of the `azd` workflow hooks — it was a manual script from an earlier iteration

**The Fix — Same-Origin Pattern:**
- Since the UI is served FROM the same Container App as the API (React build goes into `wwwroot` via vite.config.ts), we don't need an absolute URL
- Set `VITE_API_URL=` (empty string) in `.env.production.local`
- When `API_BASE = import.meta.env.VITE_API_URL || ""` evaluates to `""`, the UI makes relative API calls to the same origin it's served from
- This is CORRECT because:
  - Browser serves UI from: `https://ca-h75aielsaei6q.proudsky-ba978644.eastus2.azurecontainerapps.io/`
  - UI makes API call to: `/api/clients` (relative)
  - Browser resolves to: `https://ca-h75aielsaei6q.proudsky-ba978644.eastus2.azurecontainerapps.io/api/clients` (same origin)
- No hardcoded URLs, no stale references, always correct

**Changes Applied:**
1. Updated `scripts/preprovision.ps1` to write `VITE_API_URL=` (empty string) in `.env.production.local`
2. Updated `scripts/preprovision.sh` to write `VITE_API_URL=` (empty string) in `.env.production.local`
3. Ran `azd hooks run preprovision` to regenerate `.env.production.local` with correct config
4. Ran `azd deploy` to rebuild + redeploy the UI with the new config (37s deployment)

**Verification:**
- `curl https://ca-h75aielsaei6q.proudsky-ba978644.eastus2.azurecontainerapps.io/api/clients` → **HTTP 401 Unauthorized** (correct — API is reachable, auth required)
- `curl https://ai-policy-engine-k8m2-ca.ambitioussky-30956417.eastus2.azurecontainerapps.io/api/clients` → connection hangs (confirms old URL is dead)
- UI is now deployed with relative API URLs — should work immediately

**Key Learning — UI-to-API URL Wiring Pattern for Container Apps:**
- **When UI and API are in the same Container App:** Use relative URLs (`VITE_API_URL=` or `VITE_API_URL=window.location.origin`). Never hardcode FQDNs.
- **When UI and API are separate Container Apps:** Must inject the API URL as a Container App environment variable at runtime, OR use Terraform output to write it to a config file AFTER provisioning (postprovision hook).
- **For azd + Container Apps:** The CA FQDN is only available AFTER `azd provision`, so preprovision scripts cannot know it. If you need it at build time, use postprovision + re-trigger build/deploy.
- **Best practice:** Prefer same-origin relative URLs when possible — eliminates CORS, simplifies deployment, prevents stale URL issues.

**Files Modified:**
- `scripts/preprovision.ps1` — Added `VITE_API_URL=` to SPA env file generation
- `scripts/preprovision.sh` — Added `VITE_API_URL=` to SPA env file generation

**Status:** ✅ FIXED. Deployed. Zack should now be able to access the dashboard and see API responses.


### 2026-05-14 — Infra Fixes Committed + Pushed (3156888d)

**Coordinator shipped both URL-wiring and redirect-URI fixes per the manifest.**

- Commit **3156888d**: Infra fixes including main.tfvars.json template, preprovision scripts (VITE_API_URL empty string pattern), and postprovision scripts (Terraform-managed app redirect URI registration)
- Validated via zd up before committing — all 77 Azure resources provisioned (9m59s)
- User (Zack) plans teardown + re-deploy from scratch as final validation
- Both Sydnor decisions merged into `.squad/decisions.md` (deduplication complete)

This session resolves AADSTS500113 (no reply address) and timeout/stale URL issues. Dashboard now operational with same-origin API URL wiring and correct app registration for Entra auth.

### 2026-05-15 — Fresh Deploy Gotcha: Postprovision Variable Name Mismatch

**Context:** Zack ran a fresh `azd up` from scratch to validate prior fixes. Portal loaded but login failed.

**Root Cause:**
- Postprovision script queried `AZURE_RESOURCE_GROUP` from azd env, but Terraform outputs `resource_group_name`
- Variable name mismatch caused postprovision to skip redirect URI registration
- Result: UI rendered correctly (using Terraform-managed API app `4eda37fc`), but SPA redirect URI was missing → AADSTS500113 on login

**Investigation:**
1. Checked Container App — healthy, ingress external, targetPort 8080, 1 active replica
2. Verified API endpoint — HTTP 401 (auth required, correct)
3. Verified UI homepage — HTTP 200, loads cleanly
4. Examined deployed UI bundle — confirmed using Terraform-managed app ID (`4eda37fc`), NOT the preprovision-created app (`224ba04f`)
5. Checked API app registration — NO redirect URIs (postprovision skipped due to missing resource group)
6. Checked postprovision logs — "Skipping: AZURE_RESOURCE_GROUP not set"

**Fix Applied:**
- Updated `scripts/postprovision.ps1` line 11:
  - Before: `Select-String "^AZURE_RESOURCE_GROUP="`
  - After: `Select-String "^resource_group_name="`
- Also updated line 12:
  - Before: `Select-String "^COSMOS_ENDPOINT="`
  - After: `Select-String "^cosmos_endpoint="`
- Ran `azd hooks run postprovision` → successfully registered redirect URI on API app
- Verified: `az ad app show --id 4eda37fc...` now shows redirect URI in `spa.redirectUris`

**Verification:**
- ✅ `curl /api/clients` → HTTP 401 (correct)
- ✅ `curl /` → HTTP 200, title present
- ✅ Redirect URI registered on correct Terraform-managed app
- ✅ Container App healthy, Redis + Cosmos connected

**Key Learning:**
- Terraform output variable names (snake_case like `resource_group_name`) != azd built-in env vars (SCREAMING_CASE like `AZURE_RESOURCE_GROUP`)
- Postprovision scripts that read Terraform outputs must use Terraform's naming convention, not azd's
- This gotcha only surfaces on fresh deploys — update-style deploys often have both variable styles cached in azd env from prior runs

**Files Modified:**
- `scripts/postprovision.ps1` — Fixed resource group + cosmos endpoint variable names (lines 11-12)

**Status:** ✅ FIXED. Portal working. Login will succeed once user tests in browser.

### 2026-05-15 — Authorization 403: AIPolicy.Admin App Role Assignment Missing

**Context:** User (Zack) successfully logged into the portal (redirect URI fix worked) but received HTTP 403 (Forbidden) when navigating to the routing policies feature in the dashboard. This is an **authorization** issue, not authentication (401) or connectivity.

**Root Cause:**
- The `/api/routing-policies` endpoints require the `AdminPolicy` authorization policy (defined in `Program.cs` lines 134-135)
- `AdminPolicy` maps to the `AIPolicy.Admin` **app role** (defined in `infra/terraform/modules/identity/main.tf` lines 99-104)
- The Terraform identity module defines 3 app roles on the API app: `AIPolicy.Export`, `AIPolicy.Admin`, and `AIPolicy.Apim`
- **Only the Sample Client service principal** (client1) gets the Admin role assigned in Terraform (lines 185-189)
- **NO user** is assigned to the Admin role by default
- The postprovision script registers redirect URIs and configures Cosmos RBAC, but does NOT assign app roles to users
- Result: User logs in successfully (authentication works), but their token has NO app roles → API returns HTTP 403 on all `AdminPolicy` endpoints

**Investigation:**
1. Examined `src/AIPolicyEngine.Api/Endpoints/RoutingPolicyEndpoints.cs` — all endpoints use `.RequireAuthorization("AdminPolicy")`
2. Checked `src/AIPolicyEngine.Api/Program.cs` lines 134-135 — `AdminPolicy` requires role `AIPolicy.Admin`
3. Checked `infra/terraform/modules/identity/main.tf` — Admin role is defined on the API app (lines 99-104), but only assigned to client1 SP (lines 185-189)
4. Checked both postprovision scripts — no app role assignment logic present
5. Verified user's token has no app roles by checking Graph API appRoleAssignedTo collection — empty for the user

**Fix Applied:**
1. **Immediate Fix (Manual):** Assigned the deploying user (Zack) to the `AIPolicy.Admin` app role via Graph API:
   ```powershell
   az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<sp-object-id>/appRoleAssignedTo" --body {...}
   ```
   - API App ID: `4eda37fc-969c-4262-8569-ddcd68aa0370`
   - User Object ID: `a3abf468-f2d9-4d54-ae82-8c5def32fb91`
   - App Role ID (AIPolicy.Admin): `f5577e0a-a521-af8c-60af-1d392ff85913`
   - ✅ Role assignment succeeded

2. **Automation Fix (Postprovision):** Updated both `scripts/postprovision.ps1` and `scripts/postprovision.sh` to auto-assign the deploying user to the `AIPolicy.Admin` app role during `azd up` / `azd hooks run postprovision`:
   - Query the current signed-in user's object ID via `az ad signed-in-user show`
   - Query the API app's service principal object ID and Admin app role ID
   - Check if the user already has the role (idempotent check)
   - If not assigned, create the appRoleAssignment via Graph API
   - Pattern matches existing redirect URI registration: idempotent, fail-safe, uses Graph API via `az rest`

**Verification:**
- ✅ Manual role assignment succeeded via Graph API
- ✅ Postprovision scripts updated with idempotent app role assignment logic
- ⚠️ **User must log out and log back in** to receive a fresh token with the `AIPolicy.Admin` role claim (tokens are cached by MSAL and issued before the role assignment)

**Key Learning — App Role Assignment Pattern for Fresh Deploys:**
- **App roles are NOT scopes** — scopes are delegated permissions (OAuth2 flow), app roles are identity attributes (claim in the token)
- **Terraform can assign app roles to service principals** (Application member type), but assigning roles to users requires knowing the user's object ID at apply time (not practical for team deploys)
- **Postprovision is the correct layer** for user-based RBAC assignments because:
  - The deploying user is authenticated and available via `az ad signed-in-user show`
  - The API app ID is in azd env (Terraform output)
  - Assignment is idempotent (check before assign)
  - Fail-safe pattern: script continues if assignment fails (user can assign manually later)
- **Token refresh requirement:** App role assignments do NOT affect existing tokens. Users must log out or wait for token expiry (typically 1 hour) to receive the new role in their claims.
- **403 vs 401 diagnostics:**
  - **401 Unauthorized** = authentication failed (no token, expired token, wrong audience, missing redirect URI)
  - **403 Forbidden** = authenticated BUT not authorized (token is valid but missing required scope/role/claim)
  - For 403s, always check: (1) which policy/role the endpoint requires, (2) what claims/roles are in the user's token, (3) app role assignments on the API app's service principal

**Files Modified:**
- `scripts/postprovision.ps1` — Added AIPolicy.Admin app role assignment for deploying user (lines 133+)
- `scripts/postprovision.sh` — Added AIPolicy.Admin app role assignment for deploying user (lines 158+)

**Status:** ✅ FIXED. Zack's user now has Admin role. Must log out + log back in to get fresh token. Future deploys will auto-assign.

### 2026-05-21 — APIM Management RBAC + Runtime Wiring

**Context:** McNulty required the Container App managed identity to manage APIM API/operation policies without granting broad APIM contributor rights.

**Implementation Pattern:**
- Add a custom `azurerm_role_definition` scoped directly to the APIM service resource, with `assignable_scopes = [azurerm_api_management.this.id]` and only the eight policy-management actions McNulty approved.
- Bind it with `azurerm_role_assignment` using `role_definition_id = azurerm_role_definition.<name>.role_definition_resource_id` and `principal_id = module.compute.container_app_principal_id`.
- Output `apim_resource_id` from the gateway module for downstream consumers.

**Pitfall / Gotcha:**
- Directly feeding `module.gateway.apim_resource_id` back into the compute module creates a compute↔gateway cycle, because gateway already depends on the Container App FQDN/identity.
- The safe pattern is to derive the APIM resource ID deterministically in root (`/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ApiManagement/service/{name}`), pass that into compute for `APIM_RESOURCE_ID`, and still expose the canonical gateway-module output for scripts/consumers.
- Custom role `scope` and `assignable_scopes` must both be the APIM resource ID; widening either to the resource group/subscription silently broadens where the role can be assigned.

**Verification Reminder:**
- Re-run `terraform fmt` + `terraform validate` after adding custom roles. Validate catches the `role_definition_resource_id` vs GUID/resource ID distinction on assignments.

### 2026-05-21 — Non-AI REST APIM Policy Draft: Native APIM Default + Commented Precheck Alternative

**Context:** Zack asked for a draft Entra JWT APIM policy for non-AI REST APIs that can enforce requests-per-minute and monthly request quotas, while McNulty finalizes the backend enforcement model in parallel.

**Policy Structure Chosen:**
- Created `policies/entra-jwt-rest-policy.xml` as a sibling to the existing Entra JWT AI policy
- Default path uses native APIM `rate-limit-by-key` + `quota-by-key` keyed by `customerKey = {clientAppId}:{tenantId}`
- Outbound always logs to `POST /api/log-rest` using APIM managed identity and a fire-and-forget `send-one-way-request`
- Included a commented `ALTERNATIVE` block showing how to switch to `GET /api/precheck-rest/{clientAppId}` if the team chooses centralized policy-engine enforcement

**APIM Gotchas Discovered:**
- `quota-by-key` is a **fixed-window** quota, not a true calendar-month or billing-period counter. Using `renewal-period="2592000"` gives a 30-day window only.
- `quota-by-key` does **not** allow runtime policy expressions for `calls` or `renewal-period`, so a request-time `send-request` config lookup cannot directly drive native quota values. If limits come from the policy engine, deployment automation must render/import the policy with concrete values.
- Native APIM counters live inside APIM, not Redis. The policy engine can log derived usage via `/api/log-rest`, but it will not own the authoritative real-time counter state in this model.
- Native APIM status codes differ by policy: `rate-limit-by-key` returns 429, while `quota-by-key` returns 403 when the quota is exhausted.

**Coordination Note:** McNulty's current inbox proposal prefers `/api/precheck-rest` as the primary enforcement model. The draft policy still shipped with the requested native-APIM default and a switchable commented alternative so the coordinator can align the final direction later.
