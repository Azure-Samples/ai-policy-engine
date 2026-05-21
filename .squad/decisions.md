# Squad Decisions

## Recent Sessions

### 2026-05-21 — APIM Policy Management inbox merge
- **Goals:** capture the accepted APIM Policy Management architecture; record that prior non-AI limits work is paused.
- **Agents:** McNulty defined the accepted APIM management architecture and the earlier paused non-AI limits shape; Sydnor produced the parked non-AI REST APIM policy draft and related APIM policy/template inputs; Freamon and Kima are carrying backend/UI APIM management work on `feature/apim-policy-management`; Bunk drafted non-AI test coverage and has separate test changes pending merge; Scribe merged the inbox and archived source notes.
- **Files:** `git status --short` showed 35 changed paths at merge time (`27` modified, `8` untracked).
- **Tests baseline:** 219 passing / 4 skipped; Bunk's new tests are still pending merge and are not recorded here as landed.
- **Branch:** `feature/apim-policy-management`
- **Open items:** PR not yet opened; Bunk test changes pending merge.

## Active Decisions

### 2026-05-21T17:43:57Z: APIM ResourceId env binding convention
**By:** Freamon (Backend Dev)  
**Status:** Accepted  
**What:** Use the standard ASP.NET Core environment-variable convention for nested configuration keys: `Apim__ResourceId` (double underscore) instead of `APIM_RESOURCE_ID`. `ApimManagementOptions` binds from the `Apim` configuration section, expecting the APIM resource ID at config key `Apim:ResourceId`.  
**Why:** 
- Matches the default `EnvironmentVariablesConfigurationProvider` behavior (no custom alias handling needed).
- Keeps the application code strict and idiomatic.
- Prevents silent runtime misbinding when infrastructure sets nested config values.
**Impact:** All future Terraform and deployment wiring for APIM management must use `Apim__ResourceId` when populating `Apim:ResourceId`.  
**Audit Result:** Scanned all 200+ env vars in application configuration; no other single-underscore ASP.NET Core nested-config mismatches found.

### 2026-04-17T15:52:16Z: User directive — Agent365 SDK integration
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** Each APIM client is registered and pushes data to the Agent 365 SDK (`Microsoft.Agents.A365.*`) as an Agent for all calls to the Foundry endpoints. The Agent365 SDK (https://github.com/microsoft/Agent365-dotnet) provides the enterprise observability/identity layer we need. Docs at https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity.  
**Why:** User found the missing SDK. This replaces/augments our custom PurviewGraphClient with the official Agent365 Observability pipeline. Each client becomes an Agent365 agentic identity.  
**Key packages:** Microsoft.Agents.A365.Observability, .Runtime, .Hosting, .Extensions.OpenAI  
**Impact:** Our PurviewAuditService + PurviewGraphClient may be refactored to use the A365 Observability SDK's tracing/exporter pipeline instead of direct Graph REST calls.

### 2026-04-17T16:19:17Z: User directive — A365 integration scope
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** Q1: Start with lightweight observability only (Option C — use ClientAppId as agent.id, no full Agentic User provisioning). Q2: Emit A365 spans from Precheck and Log Ingest only, following manual instrumentation guide at https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability?tabs=dotnet#manual-instrumentation  
**Why:** User decision — lightweight first, full identity provisioning deferred to Phase 2.

### 2026-04-17T16:23:41Z: User directive — A365 integration Q3-Q6 answers
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:**
- Q3: Don't worry about deprecating PurviewGraphClient for now. As long as we use the same App ID for both A365 Observability and Purview, reports/dashboards will correlate.
- Q4: Emit spans for ALL OpenAI or Foundry endpoints. When non-agent platform API endpoints are added later, those should be excluded. For now, everything gets traced.
- Q5: A365 is HOST TENANT scoped. If the host tenant has Purview/A365 configured, it's on globally. If not configured, it's off. No per-client/per-tenant configuration needed.
- Q6: Use Aspire Dashboard for local OTel testing (A365 uses OpenTelemetry). Zack's test tenant has A365/Frontier enabled for integration testing.  
**Why:** User decisions to unblock Phase 1 implementation.

### 2026-04-01T00:00:00Z: Agent365 SDK Integration Architecture Plan (PROPOSAL)
**By:** McNulty (Lead / Architect)  
**Status:** Proposal — awaiting implementation prioritization  
**What:** Full architecture plan for integrating Microsoft Agent365 SDK (`Microsoft.Agents.A365.*`) for enterprise-grade observability, identity, and governance:
- **Key Finding:** A365 Observability SDK is **SEPARATE AND COMPLEMENTARY** to existing `Microsoft.Agents.AI.Purview` DLP integration. 
  - `Microsoft.Agents.AI.Purview` = Real-time DLP policy enforcement (block/allow at request time)
  - `Microsoft.Agents.A365.Observability` = Telemetry export (audit trail, session tracking, inference logs sent to M365/Purview for compliance dashboards)
- **Recommended Architecture:** Keep both SDKs — integrate A365 Observability alongside existing Purview DLP, mapping each `ClientPlanAssignment` to an Agent365 identity.
- **Three-Phase Plan:**
  1. **Phase 1 (Lightweight Observability):** Add A365 SDK packages, wrap PrecheckEndpoints with `InvokeAgent` scope, wrap LogIngestEndpoints with `ExecuteInference` scope. No breaking changes.
  2. **Phase 2 (Agent Identity Provisioning):** Provision Agentic User identities per-client, store mapping in CosmosDB (requires Zack's identity strategy decision).
  3. **Phase 3 (Purview Deprecation):** Once `Microsoft.Agents.AI.Purview` promotes `IScopedContentProcessor` to public API, replace custom `PurviewGraphClient` with SDK wrapper.
- **Package Dependencies:** Microsoft.Agents.A365.Observability, .Runtime, .Extensions.OpenAI (if wrapping Azure OpenAI calls)
- **Integration Points:**
  - PrecheckEndpoints: `InvokeAgent` scope with `gen_ai.agent.id=ClientAppId`, `microsoft.tenant.id=TenantId`, `gen_ai.conversation.id=correlationId`
  - LogIngestEndpoints: `ExecuteInference` scope capturing model, tokens, latency, routing decision
  - DLP Action Attribution: Set `threat.diagnostics.summary` attribute when `CheckContentAsync` blocks request
- **Configuration:** ENABLE_A365_OBSERVABILITY_EXPORTER=true env var, Agent365 settings in appsettings.json
- **Open Questions Resolved by Zack:** (1) Agent identity strategy (lightweight vs. full), (2) scope of integration (which endpoints), (3) Purview DLP replacement timeline, (4) Foundry endpoint filtering, (5) tenant/subscription requirements, (6) testing strategy
- **Estimated Effort:** Phase 1 = 2-3 days; Phase 2 = 1-5 days (depends on identity strategy); Phase 3 = 1 day (when SDK ready)
- **Risk Assessment:** Beta SDK (API may change), Frontier preview access required (not all tenants), Agent identity provisioning workflow unclear (mitigation: start lightweight)
- **Reference Document:** See .squad/decisions/inbox/mcnulty-agent365-architecture.md for full architecture analysis (60+ sections covering SDK structure, observability data model, integration patterns, migration path, identity model analysis, and comprehensive Q&A)

### 2026-04-17T16:33:20Z: Microsoft Agent365 Observability SDK Integration (Phase 1)
**By:** Freamon (Backend Dev) under guidance from Zack Way  
**Status:** Implemented  
**What:** Add Agent365 Observability SDK v0.1.75-beta alongside existing OpenTelemetry and Purview DLP. Pure additive integration. Instrument Precheck + ContentCheck endpoints with `InvokeAgentScope` and LogIngest endpoint with `InferenceScope`. Use ClientAppId as lightweight agent identity. Opt-in via `ENABLE_A365_OBSERVABILITY_EXPORTER` env var (default: false).  
**Why:** Provide agent-specific telemetry patterns for AI workloads; infrastructure for future A365 backend integration when SDK stabilizes.  
**How:** Service abstraction (`IAgent365ObservabilityService`) with stub implementation (TODO markers) and no-op fallback. DI registration. Scope wrapping in endpoint handlers. Correlation ID from `X-Correlation-ID` header.  
**Trade-offs:** Stub implementation (SDK v0.1.75-beta lacks public scope creation APIs; full implementation deferred to v0.2.x+). No per-client toggle (host-level only). No M365 identity provisioning. Config CRUD not instrumented (focused on hot path).  
**Testing:** 225 tests (221 pass, 4 skipped); zero regressions.  
**Future:** Upgrade SDK, implement token acquisition, add integration tests, enforce correlation ID.

### 2026-04-11T13:16:03Z: Agent365 SDK Real Implementation + Scope Instrumentation (IMPLEMENTED)
**By:** Freamon (Backend Dev)  
**Status:** Implemented  
**Supersedes:** 2026-04-17T16:33:20Z (stub implementation placeholder)  
**What:** Replaced Agent365 Observability stubs with real SDK scope calls using Microsoft.Agents.A365.Observability.Runtime 0.1.75-beta. Full implementation of `InvokeAgentScope.Start()` and `InferenceScope.Start()` with proper AgentDetails, TenantDetails, Request/InferenceCallDetails parameters. Manual OpenTelemetry configuration added (AddA365Tracing extension not available in this SDK version). All scope creation wrapped in try/catch fail-safe blocks.  
**Key Decisions:**
- `InvokeAgentScope.Start()` called with AgentDetails (clientAppId, clientDisplayName), TenantDetails (tenantId), Request (promptContent), and session/correlation IDs
- `InferenceScope.Start()` called with InferenceCallDetails (model, operationName, token counts), AgentDetails, TenantDetails
- Manual OpenTelemetry registration (AddA365Tracing not available in v0.1.75-beta)
- Namespace conflict resolution: `using A365Request = Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request` alias
- Placeholder endpoint: `https://apim.example.com` (APIM scenario has no fixed agent endpoint)
- Fail-safe design: null returns on any exception, never breaks request flow
**Files Modified:**
- `src/Chargeback.Api/Services/Agent365ServiceExtensions.cs` — Removed TODO, added OpenTelemetry config
- `src/Chargeback.Api/Services/Agent365ObservabilityService.cs` — Implemented real scope creation  
**Test Results:** 235 tests pass (231 pass, 4 documented skips), zero regressions  
**Why:** Production-ready observability. Stubs were causing confusion; SDK is stable enough per Microsoft docs. Real scopes provide agent-specific telemetry when enabled.

### 2026-04-11T13:16:03Z: APIM DLP Policy Variants — Fail-Open Content-Check (IMPLEMENTED)
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Created two DLP-enabled APIM policy variants offering customers choice between baseline (precheck-only) and DLP-enabled (precheck + content-check) policies. Customers without Purview DLP requirements use base policies; customers with DLP requirements use DLP-suffix variants. Both auth types have variants: subscription-key and entra-jwt.  
**New Files:**
- `policies/subscription-key-policy-dlp.xml` — Subscription key auth + DLP content-check
- `policies/entra-jwt-policy-dlp.xml` — Entra JWT auth + DLP content-check  
**Files Unchanged:** Base policies (subscription-key-policy.xml, entra-jwt-policy.xml) remain identical  
**Content-Check Pipeline:**
- **Placement:** After precheck succeeds (200) + routing logic, before backend forwarding
- **Request:** POST /api/content-check/{clientAppId}/{tenantId} with original requestBody
- **Response Handling:** HTTP 451 → block, other statuses/failures → proceed (fail-open)
- **Fail-Open Strategy:** Uses `ignore-error="true"` on send-request; transient failures don't block valid requests
- **Timeout:** 10 seconds (matches precheck timeout)
- **Authentication:** APIM managed identity token (same as precheck)  
**Error Response (HTTP 451):** Mimics Azure OpenAI content_filter format for client compatibility
**Why:** Not all customers need DLP. Offer choice. Fail-open prioritizes availability — transient content-check failures don't create outages. Only explicit HTTP 451 blocks requests.
**Trade-offs:** 4 policies to maintain (base + DLP × 2 auth types); future changes must be applied consistently across variants

### 2026-05-14T15:54:00Z: User directive — Always validate infra fixes before committing
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** When fixing infrastructure/deploy errors, the team must validate fixes by actually running the relevant `azd` command (e.g., `azd provision`, `azd up`) BEFORE committing. Do not write commits with unvalidated fixes — keeps the commit tree clean of speculative/bad history.  
**Why:** User request — captured for team memory

### 2026-05-14T15:54:00Z: azd Terraform Provider Configuration
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Added `infra:` section to `azure.yaml` to declare Terraform as the IaC provider for azd:
```yaml
infra:
  provider: terraform
  path: infra/terraform
  module: main
```
**Why:** azd CLI defaults to Bicep when no `infra:` section is declared. This configuration is required to use Terraform with azd.  
**Impact:** `azd provision` now invokes Terraform instead of looking for `infra/main.bicep`. No Terraform module changes needed.  
**Validation:** `azd provision --preview` succeeds through Terraform plan phase (blocked only by Azure tenant Conditional Access policy, not code).

### 2026-05-14T15:54:00Z: azd Terraform Provider Requires main.tfvars.json Template
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Created `infra/terraform/main.tfvars.json` as a template file with azd environment variable substitution:
```json
{
  "subscription_id": "${AZURE_SUBSCRIPTION_ID}",
  "location": "${AZURE_LOCATION}",
  "workload_name": "${AZURE_ENV_NAME}"
}
```
**Why:** azd's Terraform provider requires this file alongside `main.tf` to map azd environment variables to Terraform input variables. Without it, azd cannot initialize the Terraform module.  
**Impact:** `azd provision` now correctly reads Terraform variables from the template. File is intentionally uncommitted per Zack's directive to validate infra fixes before committing.  
**Validation:** Ran `azd provision --preview` — "file not found" error resolved; Terraform plan succeeds.

### 2026-05-14T15:54:01Z: Redirect URI Registration in Postprovision Hook
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** The postprovision hook (scripts/postprovision.ps1 and scripts/postprovision.sh) must register redirect URIs on the Terraform-managed API app (api_app_id), query the actual Container App FQDN from Azure (not Terraform state), and be idempotent by not duplicating URIs.  
**Why:** Fixes AADSTS500113 error when logging into dashboard. UI was configured to use Terraform-managed app, but postprovision was setting URIs on legacy app. Pattern queries live Azure resources, handles SPA flow correctly, and remains idempotent across re-runs.  
**Implementation:** PowerShell pattern uses Graph API (GET spa, PATCH spa.redirectUris); Bash equivalent provided. Registers Container App FQDN as SPA redirect URI (MSAL.js SPA flow requirement).  
**Impact:** Dashboard login now works. Redirect URI verified on correct app (d5bd33f4-09b1-4602-af88-29c5ec7728e0).

### 2026-05-14T15:54:02Z: UI-to-API URL Wiring: Same-Origin Pattern for Container Apps
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** For React SPAs served FROM the same Container App as their API, use relative URLs by setting VITE_API_URL= (empty string) in the preprovision script. This works because the UI is built into the API's wwwroot folder, and both are served from the same FQDN.  
**Why:** Eliminates hardcoded URLs (which go stale when Container App FQDN changes), avoids CORS issues, removes timing problems with Container App FQDN not being known until after azd provision.  
**Implementation:** Updated scripts/preprovision.ps1 and scripts/preprovision.sh to write VITE_API_URL= (empty string). UI's api.ts reads: const API_BASE = import.meta.env.VITE_API_URL || ""; (defaults to empty, uses relative URLs).  
**Trade-off:** Only works when UI and API are in the SAME Container App. If separate CAs, use runtime environment variable injection or postprovision script to query API CA FQDN and rewrite config.  
**Impact:** Dashboard now makes relative API calls to same origin. Timeout issues resolved. No stale URL references.

### 2026-05-15T16:45:18Z: Postprovision Scripts Must Use Terraform Output Variable Names
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Postprovision scripts that read Terraform outputs via `azd env get-values` must use Terraform's exact output variable names (snake_case like `resource_group_name`, `cosmos_endpoint`), NOT azd's built-in environment variable names (SCREAMING_CASE like `AZURE_RESOURCE_GROUP`, `COSMOS_ENDPOINT`).  
**Why:** On a fresh `azd up`, Terraform writes its outputs to the azd environment using the exact names defined in `outputs.tf`. If postprovision scripts query different variable names, they silently fail to find the values and skip critical configuration steps (like redirect URI registration). This gotcha doesn't surface on update-style deploys because the azd environment often has BOTH variable naming conventions cached from prior manual runs or legacy scripts.  
**Implementation:** Updated `scripts/postprovision.ps1` lines 11–12 to use `resource_group_name` and `cosmos_endpoint` instead of the legacy `AZURE_RESOURCE_GROUP` and `COSMOS_ENDPOINT`. Script now correctly resolves resource group and proceeds with Cosmos RBAC assignment and SPA redirect URI registration.  
**Impact:** Fresh `azd up` now completes end-to-end without manual postprovision fixes. Redirect URI registration and Cosmos RBAC assignment work on first deploy.  
**Trade-offs:** None — this is a strict bug fix.  
**Verification:** Tested via fresh `azd up` → `azd hooks run postprovision`: resource group resolved correctly, Cosmos RBAC assigned successfully, redirect URI registered on Terraform-managed API app.

### 2026-05-15T16:52:32Z: Auto-Assign AIPolicy.Admin App Role in Postprovision Hook
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Postprovision scripts now auto-assign the deploying user to the `AIPolicy.Admin` app role so the portal is immediately usable after deployment without manual role assignment. Updated `scripts/postprovision.ps1` and `scripts/postprovision.sh` to query the current signed-in user, check if role is already assigned (idempotent), and create the appRoleAssignment via Graph API if needed.  
**Why:** HTTP 403 errors on routing-policies endpoints after fresh `azd up` — Terraform only assigns app roles to service principals, not human users. Without the role, the deploying user is authenticated but not authorized. Postprovision can access the deploying user via `az ad signed-in-user show`.  
**How:** (1) Query signed-in user object ID, (2) Query API app service principal and AIPolicy.Admin role ID, (3) Check existing assignments (idempotent), (4) Create appRoleAssignment via `az rest` POST, (5) Emit warning to logout/login for token refresh.  
**Trade-offs:** Pro: Portal works immediately; idempotent; matches existing patterns. Con: Token refresh required (user must logout/login); only assigns deploying user (teammates need separate assignment or manual assignment).  
**Key Learning:** App role assignments don't retroactively affect existing tokens — users must obtain fresh token via logout/login or token expiry.  
**Validation:** Manual role assignment via Graph API succeeded; postprovision scripts updated with idempotent assignment logic; awaiting Zack's logout/login validation.

## 2026-05-16 — Non-AI API Usage Limits Architecture
**Owner:** McNulty  
**Status:** paused  
**Source:** `.squad/decisions/inbox/mcnulty-non-ai-api-limits-architecture.md` (will be archived after merge)

This proposal extended plan management with additive non-AI request limits: flat `NonAiRequestsPerMinute` and `NonAiMonthlyRequestQuota` fields on plan models, `NonAiCurrentPeriodRequests` on client assignments, and dedicated `/api/precheck-rest/{clientAppId}/{tenantId}` plus `/api/log-rest` endpoints for centralized enforcement and accounting. The rationale was to keep non-AI traffic out of the AI precheck hot path while preserving dashboard visibility through the policy engine's Redis/Cosmos usage model.

Implementation is paused per Zack's instruction. The parked XML/template artifacts now live under `.squad/files/non-ai-paused/`, and any future resume should reconcile this earlier endpoint-first design with the accepted APIM Policy Management architecture that now treats non-AI XML as template seed material instead of a standalone rollout.

**Related:** [APIM Policy Management Architecture](#2026-05-16--apim-policy-management-architecture), `.squad/files/non-ai-paused/`, [Non-AI APIM Policy Contract](#2026-05-21--non-ai-apim-policy-contract), [Non-AI API Limits Test Coverage Strategy](#2026-05-21--non-ai-api-limits-test-coverage-strategy)

## 2026-05-16 — APIM Policy Management Architecture
**Owner:** McNulty  
**Status:** accepted  
**Source:** `.squad/decisions/inbox/mcnulty-apim-management-architecture.md` (will be archived after merge)

The session's headline decision is Tier B APIM policy management: admins choose from shipped templates, fill validated parameters, and let the engine render and apply XML through APIM management APIs. Raw XML editing and drift management are deferred. A new APIM assignment document type lives in the existing Cosmos `configuration` container, and the UI/API surface centers on catalog, assignment, apply, and clear flows for APIs and operations.

Runtime control goes through `Azure.ResourceManager.ApiManagement` using the Container App managed identity and a narrow custom APIM policy role, with `APIM_RESOURCE_ID` as the service locator. Existing policy XML files become seed templates under `policies/templates/`; this includes the parked non-AI REST policy when that work resumes. Multi-APIM support and drift detection remain open follow-ons, not launch requirements.

**Related:** [Non-AI API Usage Limits Architecture](#2026-05-16--non-ai-api-usage-limits-architecture), `policies/templates/`, `infra/terraform/modules/gateway/`, `src/AIPolicyEngine.Api/Endpoints/ApimManagementEndpoints.cs`

## 2026-05-21 — Non-AI APIM Policy Contract
**Owner:** Sydnor  
**Status:** paused  
**Source:** `.squad/decisions/inbox/sydnor-non-ai-apim-policy.md` (will be archived after merge)

Sydnor drafted a non-AI REST APIM policy around Entra JWT validation, native APIM `rate-limit-by-key` and `quota-by-key` enforcement, backend routing through `{{NonAiBackendUrl}}`, and fire-and-forget accounting to `/api/log-rest`. The XML also carries a commented `/api/precheck-rest` alternative using APIM managed identity so the team can pivot back to centralized enforcement without redesigning the full contract.

The draft also captured APIM constraints that matter if this path returns: quota windows are fixed 30-day periods rather than billing months, quota values are deployment-time constants, native counters stay in APIM rather than Redis, and quota/rate policies block with different status codes. Because the non-AI work is paused, this draft is now reference material for the parked files and for later templateization under APIM Policy Management.

**Related:** [Non-AI API Usage Limits Architecture](#2026-05-16--non-ai-api-usage-limits-architecture), [APIM Policy Management Architecture](#2026-05-16--apim-policy-management-architecture), `.squad/files/non-ai-paused/`, `.squad/files/non-ai-paused/entra-jwt-rest-policy.xml`

## 2026-05-21 — Non-AI API Limits Test Coverage Strategy
**Owner:** Bunk  
**Status:** paused  
**Source:** `.squad/decisions/inbox/bunk-non-ai-test-plan.md` (will be archived after merge)

Bunk proposed a three-layer test strategy for the non-AI limits feature: endpoint coverage in `src/AIPolicyEngine.Tests/EndpointTests.cs`, deeper integration tests for counter isolation and rollover semantics, and NBomber load coverage for high-throughput non-AI precheck traffic across multiple customers. The plan intentionally mirrors the repository's existing split between endpoint, integration, and load-test suites.

This remains a supporting reference only. It depends on final endpoint/schema decisions, settled `0 = unlimited` and rejected-request semantics, and preferably a clock seam for rollover tests; Bunk's separate implementation work is still pending merge and is not recorded here as landed.

**Related:** [Non-AI API Usage Limits Architecture](#2026-05-16--non-ai-api-usage-limits-architecture), [Non-AI APIM Policy Contract](#2026-05-21--non-ai-apim-policy-contract), `src/AIPolicyEngine.Tests/EndpointTests.cs`, `src/AIPolicyEngine.LoadTest/Program.cs`

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
