# Decision Archive — Decisions Before 2026-05-27

This file contains architectural decisions and implementation records from the first 8 weeks of the project (2026-03-31 to 2026-05-26). These are foundational to the system but are not actively changing. Recent active decisions remain in `.squad/decisions.md`.

---

## Archived Decisions (2026-03-31 to 2026-05-26)

### 2026-05-21T22:07:10Z: Implementation status — AAA M1-M5 layer complete, ready for production

**By:** Scribe (logged from orchestration)  
**Status:** Complete  
**Summary:** Full AAA per-client authorization layer shipped with 316 passing tests. M1-M5 deliverables (AccessProfile model, CRUD endpoints, precheck integration, APIM templates, admin UI) all complete. Cascade precedence enforced at all layers. M6 (Redis caching) deferred as optional optimization. Production-ready for PR review + merge.

---

### 2026-05-21T21:48:19Z: Implementation status — AAA M1-M3 backend complete, M4-M5 parallel in-flight

**By:** Scribe (logged from orchestration)  
**Status:** In-Flight (Superseded by 2026-05-21T22:07:10Z)  
**Summary:** M1-M3 backend complete with 21-test matrix (17 passing, 4 pending M4). Freamon delivered AccessProfile model, Cosmos repo, IAccessProfileResolver, CRUD endpoints. M4/M5 in parallel (Sydnor templates, Kima UI).

---

### 2026-05-21T21:28:06Z: User directive — AAA access-profile architecture approved (M1-M6)

**By:** Zack Way (via McNulty proposal review)  
**Status:** Approved  
**Summary:** Zack approved per-client endpoint authorization architecture with dual client identity (Entra JWT + subscription-key), required plan pairing, cascade resolution (operation > API > client > plan fallback), and backward-compatible precheck integration.

**Phasing:** M1 (model + cascade service) → M2 (CRUD endpoints) → M3 (precheck integration) → M4 (log-ingest) → M5 (templates + UI) → M6 (optional Redis caching)

**Test Coverage:** 21 tests anticipated — resolver cascade, precheck backward compat, log integration, template render, end-to-end flow.

**Related Files:** `.squad/decisions/archive/mcnulty-aaa-per-client-arch.md`, `.squad/decisions/archive/mcnulty-aaa-pre-post-endpoint-contracts.md`

---

### 2026-05-21T14:16:20Z: User directive — AAA pre/post endpoint integration scope

**By:** Zack Way (via Copilot)  
**Status:** Captured (merged into approved architecture)  
**Summary:** AAA per-client access-profile layer MUST integrate into precheck + log endpoints. Endpoints accept API/operation context, resolve via Access Profiles, enforce using resolved Plan/Routing.

---

### 2026-05-21T18:35:00Z: React effect callback stabilization — /apis render-loop guardrail

**By:** Kima (UI Developer)  
**Status:** Implemented  
**Summary:** Fixed infinite render loop in Apis.tsx by eliminating circular dependency in loadInitialData useCallback. The callback depended on operationsByApi AND reset it to fresh {}, causing callback identity to change after every fetch and re-trigger the mount effect.

**Pattern:** If an effect triggers a callback that mutates local maps/arrays, keep the callback keyed to stable inputs and read the latest collection through a ref instead of adding the collection itself to deps.

**Skill:** Kima documented pattern in `.squad/skills/react-render-loop-debugging/SKILL.md`

---

### 2026-05-21T17:43:57Z: APIM ResourceId env binding convention

**By:** Freamon (Backend Dev)  
**Status:** Accepted  
**Summary:** Use ASP.NET Core standard double-underscore convention for nested config: `Apim__ResourceId` (not `APIM_RESOURCE_ID`). Matches EnvironmentVariablesConfigurationProvider behavior, keeps code idiomatic, prevents silent misbinding.

---

### 2026-04-17T15:52:16Z: User directive — Agent365 SDK integration

**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**Summary:** Each APIM client pushes data to Agent 365 SDK (Microsoft.Agents.A365.*) as an agentic identity. Provides enterprise observability/identity layer. Complements existing Purview DLP integration (separate and additive).

---

### 2026-04-17T16:19:17Z: User directive — A365 integration scope

**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**Summary:** Start with lightweight observability (Option C — use ClientAppId as agent.id, no full Agentic User provisioning). Emit A365 spans from Precheck and Log Ingest only.

---

### 2026-04-17T16:23:41Z: User directive — A365 integration Q3-Q6 answers

**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**Summary:** Q3: Don't deprecate PurviewGraphClient; same App ID correlates both. Q4: Emit spans for ALL OpenAI/Foundry endpoints. Q5: A365 is HOST TENANT scoped. Q6: Use Aspire Dashboard for local OTel testing.

---

### 2026-04-01T00:00:00Z: Agent365 SDK Integration Architecture Plan (PROPOSAL)

**By:** McNulty (Lead / Architect)  
**Status:** Proposal (superseded by Freamon's 2026-04-11 implementation)  
**Summary:** Three-phase plan for A365 SDK integration. Phase 1 (lightweight observability, 2-3 days) add SDK + instrument endpoints. Phase 2 (agent identity provisioning, 1-5 days) provision per-client Agentic Users. Phase 3 (Purview replacement, 1 day) when SDK public API ready.

---

### 2026-04-17T16:33:20Z: Microsoft Agent365 Observability SDK Integration (Phase 1)

**By:** Freamon (Backend Dev) under guidance from Zack Way  
**Status:** Implemented (stub, superseded by 2026-04-11)  
**Summary:** Add A365 SDK v0.1.75-beta alongside OpenTelemetry and Purview DLP. Pure additive. Instrument Precheck + ContentCheck with InvokeAgentScope, LogIngest with InferenceScope. Opt-in via ENABLE_A365_OBSERVABILITY_EXPORTER. 225 tests pass, zero regressions.

---

### 2026-04-11T13:16:03Z: Agent365 SDK Real Implementation + Scope Instrumentation (IMPLEMENTED)

**By:** Freamon (Backend Dev)  
**Status:** Implemented  
**Summary:** Replaced stubs with real SDK scope calls. Full InvokeAgentScope/InferenceScope implementation with proper AgentDetails, TenantDetails, Request/InferenceCallDetails. Manual OTelemetry config (AddA365Tracing unavailable in v0.1.75-beta). Namespace conflict resolved (A365Request alias). 235 tests pass, zero regressions.

---

### 2026-04-11T13:16:03Z: APIM DLP Policy Variants — Fail-Open Content-Check (IMPLEMENTED)

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Created two DLP-enabled APIM policy variants (subscription-key-policy-dlp.xml, entra-jwt-policy-dlp.xml) offering choice between baseline (precheck-only) and DLP-enabled (precheck + content-check). Content-check fails-open on transient errors, only blocks on HTTP 451.

---

### 2026-05-14T15:54:00Z: User directive — Always validate infra fixes before committing

**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**Summary:** When fixing infrastructure/deploy errors, validate fixes by running relevant azd command (e.g., azd provision, azd up) BEFORE committing. Keeps commit tree clean of speculative/bad history.

---

### 2026-05-14T15:54:00Z: azd Terraform Provider Configuration

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Added `infra:` section to azure.yaml to declare Terraform as IaC provider. azd CLI defaults to Bicep when no `infra:` section declared. Configuration required to use Terraform with azd.

---

### 2026-05-14T15:54:00Z: azd Terraform Provider Requires main.tfvars.json Template

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Created infra/terraform/main.tfvars.json as template file with azd environment variable substitution. azd's Terraform provider requires this file alongside main.tf to map azd env vars to Terraform input variables.

---

### 2026-05-14T15:54:01Z: Redirect URI Registration in Postprovision Hook

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Postprovision hook must register redirect URIs on Terraform-managed API app, query actual Container App FQDN from Azure (not Terraform state), and be idempotent. Fixes AADSTS500113 login error. Pattern queries live Azure resources, handles SPA flow correctly.

---

### 2026-05-14T15:54:02Z: UI-to-API URL Wiring: Same-Origin Pattern for Container Apps

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** For React SPAs served FROM same Container App as API, use relative URLs by setting VITE_API_URL= (empty string). Eliminates hardcoded URLs (go stale), avoids CORS, removes timing problems with Container App FQDN discovery.

---

### 2026-05-15T16:45:18Z: Postprovision Scripts Must Use Terraform Output Variable Names

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Postprovision scripts must use Terraform's exact output variable names (snake_case like resource_group_name), NOT azd's environment variable names (SCREAMING_CASE like AZURE_RESOURCE_GROUP). Fresh azd up fails silently if names mismatch because Terraform outputs aren't in azd environment yet.

---

### 2026-05-15T16:52:32Z: Auto-Assign AIPolicy.Admin App Role in Postprovision Hook

**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**Summary:** Postprovision scripts now auto-assign deploying user to AIPolicy.Admin app role so portal is immediately usable. Queries signed-in user, checks existing assignments (idempotent), creates appRoleAssignment via Graph API if needed. Trade-off: User must logout/login for token refresh.

---

## 2026-05-16 — Non-AI API Usage Limits Architecture

**Owner:** McNulty  
**Status:** Paused  

Proposed additive non-AI request limits (flat NonAiRequestsPerMinute, NonAiMonthlyRequestQuota fields on plan, dedicated precheck-rest endpoints). Implementation paused per Zack's instruction. Artifacts parked in `.squad/files/non-ai-paused/`. Future resume should reconcile with accepted APIM Policy Management architecture.

---

## 2026-05-16 — APIM Policy Management Architecture

**Owner:** McNulty  
**Status:** Accepted  

Tier B APIM policy management: admins choose shipped templates, fill parameters, engine renders + applies XML via APIM APIs. Raw XML editing and drift management deferred. Runtime control via Azure.ResourceManager.ApiManagement SDK with managed identity. Existing policy XML becomes seed templates. Multi-APIM support and drift detection deferred.

---

## 2026-05-21 — Non-AI APIM Policy Contract

**Owner:** Sydnor  
**Status:** Paused  

Drafted non-AI REST APIM policy around Entra JWT validation, native APIM rate-limit/quota enforcement, backend routing, and fire-and-forget accounting. Contains commented precheck-rest alternative for future pivot. Captures APIM constraints (30-day fixed quota windows, deployment-time constants). Reference material for parked files and future templateization.

---

## 2026-05-21 — Non-AI API Limits Test Coverage Strategy

**Owner:** Bunk  
**Status:** Paused  

Three-layer test strategy for non-AI limits: endpoint coverage, integration tests for counter isolation/rollover, NBomber load coverage. Mirrors existing repository split (endpoint/integration/load-test). Supporting reference only; implementation pending.

---

## Governance

- All meaningful changes require team consensus
- Archive decisions older than 30 days to preserve decisions.md under 20KB
- Document architectural decisions for team memory
- Keep history focused on work, decisions focused on direction
