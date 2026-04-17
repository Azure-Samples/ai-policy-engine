# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `infra/` — Azure Bicep templates (my primary workspace)
- `policies/` — APIM policy definitions
- `src/Chargeback.AppHost/` — Aspire orchestration
- `src/Dockerfile` — Container build
- `scripts/` — Deployment and utility scripts

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-31 — Phase 0 Complete: Backend Storage Architecture Established

**Phase 0 Status:** ✅ COMPLETE (Freamon + Bunk)

The backend storage architecture has been refactored from Redis-only to a durable CosmosDB source-of-truth pattern with Redis as a write-through cache. Infrastructure implications:

**What Sydnor Needs to Know:**
- **New CosmosDB Container:** `configuration` container added to `ConfigurationContainerInitializer`. Stores plans, clients, pricing, usage policies, and future routing policies. Partitioned by `/id` (document ID).
- **Redis Remains Caching Layer:** All reads still go through Redis first. Write-through cache means writes hit Cosmos first, then Redis is updated.
- **Startup Services:** New services on startup: `RedisToCosmossMigrationService` (one-time migration of existing Redis data) and `CacheWarmingService` (populate Redis from Cosmos). Both are idempotent.
- **No New Azure Resources:** Existing Cosmos + Redis still sufficient. No changes to Bicep or Azure resource provisioning needed.
- **Container Initialization:** `ConfigurationContainerInitializer.cs` now creates `configuration` container with proper schema initialization.
- **Deployment Impact:** Minimal. Startup is slightly slower due to cache warming, but no downtime required. Redis data is automatically migrated on first startup.

**For Phase 1 Onwards:**
- Model Routing will add `routing-policies` to the `configuration` container.
- Multiplier Pricing will extend existing `pricing` and `plans` documents with new fields.
- All future configuration entities will use the same repository pattern + caching layer.

**Test Results:** 129/129 tests pass (36 new Phase 5 tests for repositories/migration/warmup).

### 2026-03-31 — Phase 1 Complete: Model Routing Architecture Ready for Phase 3

**Phase 1 Status:** ✅ COMPLETE (Freamon + Bunk)

All model routing and per-request multiplier pricing features are complete and tested. Backend API contracts finalized. Infrastructure requirements unchanged (uses existing CosmosDB + Redis).

**What Sydnor Needs to Know for Phase 3:**

- **Backend is Ready:** All 7 routing enforcement endpoints ready (F2.1–F2.7). No more breaking changes — API contracts stable.
- **Precheck Response Extended:** New fields available: `routedDeployment`, `requestedDeployment`, `routingPolicyId`. APIM policies can use these for access control decisions.
- **Rate Limiting by Deployment:** Rate limit checks now deployment-scoped. The routed deployment is the one that gets rate-limited, not the originally requested model.
- **Multiplier Billing Fields:** Audit trail includes pricing data: `Multiplier`, `EffectiveRequestCost`, `TierName`. APIM policies can log these for chargeback.
- **Deployment Discovery:** All routing evaluations validate against Foundry deployments. Empty Foundry = strict validation failure (no phantom references).
- **No New Azure Resources:** Phase 3 uses existing resources. APIM policies are stateless — they call precheck and log ingest endpoints.
- **Backward Compat:** All new fields are nullable. Existing clients continue to work without changes.

**Ready for Phase 3 Deployment:**
- Deploy Chargeback.Api with Phase 2 enforcement active
- Configure APIM policies to call precheck endpoint for authentication/authorization
- APIM policies log routing + pricing metadata via log ingest endpoint
- No schema migrations needed — CosmosDB containers already configured

**Test Results:** 200/200 tests pass (30 new Phase 2 integration tests from Bunk B5.7 + B5.8).

### 2026-03-31 — Phase 3 Complete: APIM Auto-Router Policies (S3.1–S3.3)

**Phase 3 Status:** ✅ COMPLETE (Sydnor)

Both APIM policy files updated with auto-router support. Identical routing logic in both policies.

**What Changed:**

- **Inbound — Auto-Router Logic (after precheck, before backend):**
  - Extracts `routedDeployment` from precheck 200 response using `Body.As<JObject>(preserveContent: true)`
  - If `routedDeployment` is non-empty AND differs from the client's `deploymentId`: saves original as `originalDeploymentId`, updates `deploymentId`, rewrites URL path via `<rewrite-uri>`
  - If `routedDeployment` is null/empty or matches requested deployment: no-op, existing behavior preserved
  - Comment block explains auto-router semantics: no forced downgrades, pass-through for explicit deployments

- **Outbound — Extended Log Payload:**
  - Added `requestedDeploymentId` (original client ask) and `routedDeployment` (precheck recommendation) to the fire-and-forget `/api/log` POST
  - `requestedDeploymentId` = `originalDeploymentId` if routing happened, else `deploymentId`
  - `routedDeployment` = precheck value or empty string

**Design Decisions:**
- `preserveContent: true` on response body read ensures the body stream isn't consumed before backend routing
- `&amp;&amp;` used in XML condition (proper XML entity encoding for `&&`)
- URL rewrite uses `path.Replace()` — safe no-op when URL doesn't contain `/deployments/{id}/` (e.g., Responses API body-based model)
- `originalDeploymentId` only set inside routing `<when>` block — log payload checks `ContainsKey` to handle both routed and non-routed paths

**Files Modified:**
- `policies/subscription-key-policy.xml` — +43 lines (S3.1 + S3.3)
- `policies/entra-jwt-policy.xml` — +43 lines (S3.2 + S3.3)

### 2026-03-31 — Session Complete: All 5 Phases Delivered

**Project Status:** ✅ COMPLETE

All work is done. Phase 3 (APIM auto-router policies) is complete, Phase 4 (Frontend) is complete, Phase 5 (testing + validation) is complete. 222 tests passing. Backend routing and multiplier pricing features fully operational. APIM policy layer ready for production deployment.

**Sydnor's Contributions:**
- Phase 3 (S3.1–S3.3): APIM auto-router policy implementation, request logging extended with routing metadata

**What's Ready for Deployment:**
- Backend API (Chargeback.Api) with all routing/pricing/enforcement endpoints
- APIM policies (subscription-key, entra-jwt) with auto-router logic
- Frontend UI (React) with adaptive billing dashboards and routing policy management
- CosmosDB configured with configuration containers
- 222 integration + unit tests, all passing
- Performance validated: routing sub-microsecond, precheck <5ms p99

**Next Phase (Future):**
- Policy engine for enforced model rewrites
- Health check integration for fallback routing
- Load-based routing for PTU optimization

### 2026-04-01 — Infrastructure Hardening: 5 Validated Findings Fixed

**Findings Fixed:** #6, #8, #9, #10, #17

**#6 — APIM Least-Privilege Roles (CRITICAL):**
- **Removed** Contributor role assignment on entire RG for APIM — over-privileged and unnecessary.
- **Fixed wrong GUID**: Key Vault Secrets User assignment was using `7f951dda` (AcrPull!) instead of `4633458b` (Key Vault Secrets User). Bug in original Bicep.
- **Upgraded** OpenAI role from `Cognitive Services User` to `Cognitive Services OpenAI User` — narrower scope, least-privilege.
- APIM now has exactly 2 roles: Key Vault Secrets User + Cognitive Services OpenAI User.
- APIM→Container App calls use Entra ID token acquisition, not Azure RBAC — no role needed.

**#8 — Cosmos Keys Disabled (CRITICAL):**
- Set `disableLocalAuth: true` on Cosmos account. Managed identity only (DefaultAzureCredential already in use).
- Connection strings with keys can no longer authenticate. All access via Entra ID.

**#9 — ACR Managed Identity Pull (CRITICAL):**
- Replaced admin username/password ACR pull with `identity: 'system'` on Container App registry config.
- Removed `acrUsername` and `acrPassword` params from Bicep + deployment scripts.
- Added `acrName` param + conditional AcrPull role assignment for Container App managed identity.
- Updated `parameter.json`, `parameter.sample.json`, `setup-azure.ps1`, `setup-azure.sh`.

**#10 — Health Checks Unconditional (IMPORTANT):**
- Removed `if (app.Environment.IsDevelopment())` gate from `MapDefaultEndpoints()` in ServiceDefaults.
- Health endpoints `/health` and `/alive` now always registered with `.AllowAnonymous()`.
- Required for container orchestration liveness/readiness probes in production.

**#17 — Streaming Parser Hardened (IMPORTANT):**
- Changed chunk filter from `l.Contains("{")` to `l.Contains("\"usage\"")` in both APIM policies.
- Now only parses SSE chunks that contain the `"usage"` field, not arbitrary JSON (error responses, etc.).
- Applied identically to `subscription-key-policy.xml` and `entra-jwt-policy.xml`.

**Verification:** 198/198 tests pass. Build clean. Zero regressions.

**Key Learnings:**
- The original Key Vault role assignment for APIM was silently using the AcrPull GUID (`7f951dda`). Always cross-check role GUIDs against `roles.json` — don't trust inline comments.
- `Cognitive Services User` is broader than `Cognitive Services OpenAI User` — for APIM calling only OpenAI, the narrower role suffices.
- Container Apps support `identity: 'system'` in registry config — no need for admin creds or secretRef.
- Aspire ServiceDefaults template gates health checks behind `IsDevelopment()` by default — must override for production container deployments.

### 2026-04-01 — AADSTS50011 Redirect URI Mismatch Fixed

**Bug:** After Bicep deployment, the React SPA failed to authenticate — Entra ID returned `AADSTS50011` because the redirect URI didn't match any configured URIs on the app registration.

**Root Cause (two issues):**

1. **Wrong app registration (PowerShell only):** `setup-azure.ps1` Phase 8 set SPA redirect URIs on `$client1ObjId` (Chargeback Sample Client) but NOT on `$apiObjId` (Chargeback API). The frontend's MSAL config uses the API app's client ID (`VITE_AZURE_CLIENT_ID=$apiAppId`), so the redirect must be on the API app. The bash version (`setup-azure.sh`) already correctly targeted both apps — the PowerShell script was missing the API app.

2. **Trailing slash mismatch (both scripts):** Both `setup-azure.ps1` and `setup-azure.sh` registered URIs with trailing slashes (`https://host/`) but the MSAL SPA sends `window.location.origin` which returns `https://host` without a trailing slash. Entra ID performs exact matching on SPA redirect URIs.

**Fix:**
- `setup-azure.ps1` Phase 8: Added API app redirect URI configuration (Graph PATCH on `$apiObjId`) before the client app 1 configuration. Removed trailing slashes.
- `setup-azure.sh` Phase 8: Removed trailing slashes from redirect URIs.
- `deploy-container.ps1`: Already correct — no changes needed (targets API app, no trailing slashes).

**Key Learnings:**
- MSAL SPA `redirectUri: window.location.origin` always returns URLs without trailing slashes. Entra ID SPA redirect URI matching is exact — `https://host/` ≠ `https://host`.
- When the frontend uses `VITE_AZURE_CLIENT_ID` = API app ID, the SPA redirect URI must be registered on that API app registration, not just on client apps.
- Always cross-check PowerShell and bash versions of deployment scripts — they can drift independently.
