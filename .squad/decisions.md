# Squad Decisions

## Active Decisions

### 2026-03-31T15:46:00Z: Multiplier pricing model — per-request, not per-token
**By:** Zack Way  
**Status:** Accepted  
**What:** The multiplier model is per-request, not per-1K-token. Each request costs based on the model's multiplier. Example: GPT-4.1 = 1.0x (baseline), GPT-4.1-mini = 0.33x — so every 3 requests to mini reduces the available request counter by 1 instead of 1:1. Monthly limits are request-based with model cost multipliers applied. This is the GHCP-style approach.  
**Why:** User design decision — core billing model for the pricing feature.

### 2026-03-31T15:46:01Z: Routing policies need CosmosDB persistence
**By:** Zack Way  
**Status:** Accepted  
**What:** Routing policies should be stored in both Redis (hot path) AND CosmosDB (audit trail). Need policy change history for compliance/audit.  
**Why:** User design decision — routing policy changes are auditable events.

### 2026-03-31T15:50:00Z: Routing uses Foundry deployments, no pattern matching
**By:** Zack Way  
**Status:** Accepted  
**What:** Model routing should map accounts to specific deployments retrieved from Foundry endpoints configured in the environment. No glob or regex pattern matching needed — deployments are known entities from Foundry. The routing modes are: per-account, enforced, or QoS-based. Deployment lists come from the Foundry endpoint, not from manual configuration.  
**Why:** User design decision — simplifies routing rules and grounds them in real deployed models.

### 2026-03-31T15:52:00Z: Rate limits apply to routed deployment
**By:** Zack Way  
**Status:** Accepted  
**What:** When model routing redirects a request, rate limits (RPM/TPM) apply to the routed deployment — the one that actually hits the backend — not the model the client originally requested.  
**Why:** User design decision — rate limiting should reflect actual backend load.

### 2026-03-31T15:55:00Z: CosmosDB is source of truth, Redis is cache only
**By:** Zack Way  
**Status:** Accepted  
**What:** All configuration data — plans, accounts/client assignments, pricing, routing policies — MUST persist to CosmosDB as the durable store. Redis should be used only as a cache for fast read operations. The current pattern of Redis-only storage for plans/pricing/clients is wrong — that data won't survive upgrades, recycles, or Redis eviction. CosmosDB is the source of truth; Redis is the hot-path read cache populated from Cosmos.  
**Why:** User directive — fundamental architecture correction. Configuration data must survive infrastructure lifecycle events (upgrades, recycles, cache eviction). This is a significant change to the existing storage pattern.  
**Phase:** Phase 0 (COMPLETE) — Repository pattern implemented, all endpoints refactored, 129/129 tests pass.

### 2026-03-31T16:05:00Z: Adaptive billing UI based on plan configuration
**By:** Zack Way  
**Status:** Accepted  
**What:** The dashboard and billing UI should adapt based on what's configured across all plans:
- If ALL plans use multiplier billing → show only request-based views (no token UI)
- If ALL plans use token billing → show only token-based views (no multiplier UI)
- If MIXED (some plans use multiplier, some use token) → show hybrid view with both billing models visible
This applies to dashboards, usage views, client detail pages, and export options. The UI should not show irrelevant billing modes — keep it clean for the common case but support hybrid when needed.  
**Why:** User design decision — UX should adapt to actual configuration, not force users to see billing models they don't use.

### 2026-03-31T16:10:00Z: Architecture v2 — CosmosDB Source of Truth + Repository Pattern (ACCEPTED)
**By:** McNulty (revised)  
**Status:** Accepted  
**Supersedes:** v1 proposal (mcnulty-model-routing-pricing-architecture.md)  
**Summary:** Three bodies of work revised to incorporate Zack Way's design decisions:
1. **Storage Architecture Migration (Phase 0 — COMPLETE):** CosmosDB is durable source of truth; Redis is write-through cache. New repository pattern (`IRepository<T>`, `CachedRepository<T>` wrapper), startup migration service, cache warmup service. All endpoints refactored. 36 new tests. 129/129 tests pass.
2. **Model Routing (Phase 1 — COMPLETE):** Map accounts to known Foundry deployments (no pattern matching). Three routing modes: per-account, enforced, QoS-based. Rate limits apply to routed deployment. New `ModelRoutingPolicy` entity in repositories. 41 new tests. 170/170 tests pass.
3. **Per-Request Multiplier Pricing (Phase 2 — COMPLETE):** `effective_cost = 1 × model_multiplier` per request (not per-token). GPT-4.1 = 1.0x, GPT-4.1-mini = 0.33x. Monthly quotas are request-based, not token-based. 30 new integration tests. 200/200 tests pass.

**Architectural Pattern:**
- Write path: API → CosmosDB (persist) → Redis (update cache)
- Read path: API → Redis (cache hit) → CosmosDB (cache miss, then populate Redis)
- Startup: CosmosDB → Redis (warm the cache)

**Key Extension Points:**
- `IRepository<T>` for pluggable persistence
- `PrecheckEndpoints.cs` — routing decisions + rate limit enforcement
- `ChargebackCalculator` — extend cost calculation with multipliers
- `AuditLogDocument` / `BillingSummaryDocument` — extend with routing + multiplier fields

### 2026-03-31T16:20:00Z: Empty Foundry = reject routing rules (ACCEPTED)
**By:** Zack Way (via Copilot directive)  
**Status:** Accepted  
**Rationale:** If Foundry returns no deployments, routing policy validation should reject all rules. This is a degenerate case — you can't get a Foundry endpoint without deploying at least one model. Empty Foundry = misconfiguration, not a valid state to save routing policies against.  
**Implementation:** Option A (strict). `RoutingPolicyEndpoints.ValidateDeployments` rejects empty deployment sets per `RoutingPolicyValidator` service logic. Prevents phantom deployment references. Found and validated by Bunk during B5.6 test writing.  
**Phase:** 2 (enforcement) — validation now strictly enforced in PrecheckEndpoints routing evaluation.

### 2026-03-31T16:40:00Z: Model routing is auto-router, not enforced rewrite (initial scope) (ACCEPTED)
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** The initial model routing feature is an AUTO-ROUTER, not enforced model rewriting:
1. Users send requests WITHOUT a model/deployment specified → system picks the deployment based on their plan's routing policy
2. Fallback routing if a model is unavailable (future — needs health checks, not yet built)
3. NOT about forcing users to a different model than what they requested. If they ask for GPT-4, they get GPT-4.
4. Enforced rewrite (redirect GPT-4 → GPT-4o-mini based on policy) is a FUTURE policy engine feature — there's a whole policy engine to build for that.
5. Load-based routing to PTUs is tabled for now.
6. The APIM architecture (precheck returns routedDeployment, APIM applies it) is correct — it's the necessary plumbing. But the initial implementation should be auto-routing, not rewrite enforcement.
**Why:** User scope clarification — defines the boundary between initial routing feature and future policy engine work.

### 2026-03-31T17:30:00Z: Code Review Complete — 11 Findings Fixed (APPROVED FOR MERGE)
**By:** McNulty (Lead / Architect)  
**Status:** Implementation Complete  
**What:** All 11 code review findings from comprehensive review (Phases 0–4) are now fixed:
- **3 Blocking:** B1 (precheck quota enforcement), B2 (billingPeriod type), B3 (RouteRule fields)
- **8 Should-Fix:** S1 (dead code), S2 (JSON injection), S3 (race condition), S4 (audit trail), S5 (type consolidation), S6 (type safety), S7 (cache thread safety), S8 (error messages)
- **5 Nice-to-Have:** N1–N5 tabled for future sprint
**Why:** Production readiness. All issues resolved with zero regressions. Backend: 198/198 tests pass. Frontend: tsc clean, vite build clean. Ready for deployment.
**Implementation:** Freamon fixed 6 backend findings, Kima fixed 5 frontend findings. Both agents delivered on schedule.
**Architecture Impact:** No architectural changes — all fixes are bug corrections and code cleanup.
**Decision:** APPROVED FOR MERGE. Deploy backend + frontend together. Schedule N1–N5 for next sprint.

### 2026-04-01T17:52:00Z: Re-Review Verdict — All 11 Findings Verified (APPROVED FOR DEPLOYMENT)
**By:** McNulty (Lead / Architect)  
**Status:** Approved  
**What:** Independent re-review of all 11 code review fixes. All blockers, should-fixes, and nice-to-haves verified:
- **B1 (Precheck Quota Enforcement):** ✅ FIXED. `PrecheckEndpoints.cs` lines 117–127 correctly check `UseMultiplierBilling && MonthlyRequestQuota > 0`, compute `effectiveRequests`, return 429 when quota exceeded. Positioned after token quota check.
- **B2 (BillingPeriod Type):** ✅ FIXED. `types.ts` line 303 defines `billingPeriod: string` with correct format. `RequestBilling.tsx` line 126 renders as plain string. No property access errors.
- **B3 (RouteRule Fields):** ✅ FIXED. `types.ts` lines 245–246 defines `priority: number` and `enabled: boolean`. `RoutingPolicies.tsx` includes editable inputs (priority lines 350–359, enabled lines 336–345).
- **S1 (Dead Code Cleanup):** ✅ FIXED. `Repositories/` directory removed, zero namespace references remain.
- **S2 (JSON Injection Prevention):** ✅ FIXED. Both APIM policies use `JObject` construction, no string interpolation of user values.
- **S3 (Race Condition Fix):** ✅ FIXED. `ConfigurationContainerProvider.cs` uses `SemaphoreSlim(1,1)` with proper double-check locking and `finally` cleanup.
- **S4 (Audit Trail Complete):** ✅ FIXED. `routingPolicyId` flows end-to-end: precheck → APIM → log → storage. All 6 steps verified.
- **S5 (Type Consolidation):** ✅ FIXED. `ModelPricing` base type includes `multiplier: number` and `tierName: string`. No extended types needed.
- **S6 (Type Safety):** ✅ FIXED. `PlanCreateRequest` and `PlanUpdateRequest` both include routing and billing fields.
- **S7 (Cache Thread Safety):** ✅ FIXED. `ChargebackCalculator.cs` uses proper `lock()` with double-check pattern and no bare writes outside lock.
- **S8 (Error Messages):** ✅ FIXED. `parseErrorMessage` helper applied consistently across all 27 API functions.
**Why:** Production gate. Verify zero regressions and no new issues before deployment.
**Result:** No regressions, no new issues found. Codebase is clean and ready for production.
**Decision:** ✅ APPROVED FOR DEPLOYMENT. Merge and deploy backend + frontend together immediately.

### 2026-04-01T18:00:00Z: API is single-tenant — IDOR finding dismissed
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** The management API is single-tenant only (single-tenant app registration). Secondary tenants cannot authenticate to the API — they only communicate with the APIM gateway. APIM calls the API using its own Managed Identity credentials, unwrapping client auth data. This means IDOR vulnerabilities around tenant scoping are not applicable — there is no multi-tenant auth surface on the API itself.  
**Why:** Code review finding dismissal — reviewers incorrectly assumed multi-tenant API access model.

### 2026-04-01T18:49:00Z: Product rename to Azure AI Gateway Policy Engine
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** The product is being renamed from "AI Policy Engine" to "Azure AI Gateway Policy Engine". All references in README, docs, and descriptions should use the new name. A full rename of projects, namespaces, containers, etc. will follow as a separate task after current bug fixes complete.  
**Why:** User directive — product branding update.

### 2026-04-09T11:12:38Z: Purview two-phase flow architecture
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** The API is called at two distinct points in the request flow:
1. **Precheck (request time):** When APIM sends the prompt BEFORE the LLM call. This is where content blocking should be evaluated (protectionScopes/compute + processContent). If blocked, return a block verdict to APIM — no audit event should be emitted.
2. **Log ingest (response time):** When APIM sends both prompt + LLM response. This is where audit events should be emitted (contentActivities). Only emit if the request was actually processed by the LLM endpoint — no audit on blocked requests.  
**Implication for IPurviewAuditService:**
- Add CheckContentAsync(content, tenantId, clientDisplayName, ct) returning a block verdict — called from PrecheckEndpoints.cs
- EmitAuditEventAsync stays as-is — called from LogIngestEndpoints.cs
- The PURVIEW_BLOCK_ENABLED flag gates whether precheck actually calls processContent vs no-op  
**Why:** Avoids emitting audit events for requests that were never processed, and ensures blocking happens at the correct point in the flow where APIM can act on the verdict.

### 2026-04-09T11:13:21Z: Purview event attribution - use client name, not our app name
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** Purview audit events and block alerts must be attributed to the CALLING CLIENT, not to our middleware app. The client's display name (from ClientPlanAssignment.DisplayName) must flow into the Graph API request payloads as the event source — specifically into integratedAppMetadata.name and protectedAppMetadata.name in the processContent and contentActivities request bodies. This ensures that in the Purview compliance portal, violations and audit trails show the client application ("Contoso AI Bot", "Fabrikam Chat App") as the source, not "Chargeback API" or our service identity.  
**Why:** User requirement — compliance officers and security teams reviewing Purview events need to see which client caused an event, not which middleware forwarded it. Without this, all events look like they came from our single service.  
**Implementation note:** PurviewSettings.AppName is already set per-event to clientDisplayName. The PurviewGraphClient must use settings.AppName in the integratedAppMetadata and protectedAppMetadata name fields when building the Graph API requests.

### 2026-04-09T11:15:05Z: Purview AppName = client's Entra app registration name
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** The AppName in Purview audit events is NOT our service name. The client IS an Entra app registration. Their display name (ClientPlanAssignment.DisplayName) IS the app name in Entra's sense. In the Graph API request bodies, integratedAppMetadata.name and protectedAppMetadata.name should reflect the CLIENT'S Entra app display name — the app that called our API. We are transparent middleware; Purview should see events attributed to the calling app registration, not to us.  
**Why:** Conceptual clarity — the client is an app registration (an Azure AD app). Their ClientPlanAssignment.DisplayName is the name of that app registration. Purview compliance events should identify the app that generated the AI interaction, which is the client's Entra app, not our chargeback/APIM proxy service.  
**Implementation:** clientDisplayName flows into PurviewSettings.AppName per-event, then into the JSON request payload's integratedAppMetadata.name and protectedAppMetadata.name fields in PurviewGraphClient.

### 2026-04-01T00:00:00Z: PurviewGraphClient — Build Own Graph REST API Client
**By:** Freamon (Backend Dev)  
**Status:** Accepted  
**What:** Microsoft.Agents.AI.Purview rc6 exposes only public surface and keeps all content-processing types internal. Solution: build our own PurviewGraphClient that directly calls the Purview/Graph REST API endpoints, mimicking what the SDK does internally without wrapping SDK-internal types.  
**New Components:**
- `PurviewGraphClient.cs` — internal sealed class calling three Graph endpoints: contentActivities (audit trail), processContent (inline DLP), protectionScopes/compute (scope check)
- `PurviewModels.cs` — internal DTOs with @odata.type discriminators for Graph API JSON serialization
- Modified `PurviewAuditService.cs` — real Graph API implementation with token acquisition and JWT claim decoding (oid → userId, tid → tenantId). When blockEnabled=true: calls GetProtectionScopesAsync → ProcessContentAsync; logs PURVIEW_BLOCK_VERDICT if BlockAccess/Block action returned.  
**Exception Handling:** HTTP responses mapped to SDK's public exception types (429 → PurviewRateLimitException, 401/403 → PurviewAuthenticationException, etc.)  
**Migration Path:** Once Microsoft.Agents.AI.Purview promotes IScopedContentProcessor to public surface, PurviewGraphClient can be replaced with the SDK wrapper.  
**Why:** SDK internal types are unreachable; building our own client avoids wrapper complexity and maintains control over the implementation.

### 2026-04-01T00:00:00Z: ADR: PurviewAuditService Improvements — Typed Exceptions + Per-Client AppName + SDK Integration
**By:** McNulty (Lead / Architect)  
**Status:** Accepted  
**What:** Four improvements required: (1) Dynamic per-client AppName (client DisplayName instead of static config), (2) Typed exception handling for all Microsoft.Agents.AI.Purview exceptions, (3) Actual IScopedContentProcessor.ProcessMessagesAsync call, (4) Opt-in block verdict handling.  
**Key Decisions:**
- Add `string clientDisplayName` parameter to EmitAuditEventAsync (explicit call site, not a wrapper record)
- Channel item type stays `LogIngestRequest` with internal PurviewAuditItem wrapper (keeps interface clean)
- Introduce `PurviewSettingsTemplate` (invariant config) to construct per-event `PurviewSettings` with dynamic AppName = clientDisplayName
- Do NOT register IScopedContentProcessor in DI; instantiate directly per background event
- Exception handling: Purview is NEVER on hot path. Retry with backoff (max 3) on rate-limit/job-limit only; other exceptions silent-fail with logging
- No Polly; use exponential backoff (1s, 2s, 4s) in-loop
- Block verdicts at log-ingest time: log WARNING and set Redis key (precheck enforcement is future work)  
**Why:** Typed exceptions prevent silent failures; per-event settings enable proper attribution; background processing keeps precheck latency unaffected.

### 2026-04-09T00:00:00Z: Structural Decisions: PurviewGraphClient Test Coverage
**By:** Bunk (Tester)  
**Status:** Accepted  
**Decisions:**
1. **Test internal types directly via InternalsVisibleTo** — PurviewGraphClient, PurviewTokenInfo, DTOs are internal sealed. Use InternalsVisibleTo("Chargeback.Tests") to test directly (more precise, easier to debug than testing indirectly through PurviewAuditService).
2. **Skip EmitCoreAsync_CallsGraphClient** — no IPurviewGraphClient seam exists yet. Add architecture note: add IPurviewGraphClient interface with SendContentActivitiesAsync, ProcessContentAsync, GetProtectionScopesAsync; inject via constructor; mock in tests. This is a meaningful refactor that should be reviewed by McNulty.
3. **Test blockEnabled=true without Graph integration** — assert no exceptions on emit and dispose (don't crash when Graph unavailable). Actual block verdict logging path requires real/mocked Graph response with policyActions — track separately.
4. **Use custom CapturingLogger<T>** — simpler than FakeLogger (no log level filtering setup needed), captures all levels, zero-cost.  
**Why:** Direct tests are more precise; skips document exactly what's needed for future refactor; custom logger avoids setup complexity.

### 2026-04-01T00:00:00Z: CheckContentAsync Test Coverage — Silent-Fail Resilience Pattern
**By:** Bunk (Tester)  
**Status:** Implemented  
**What:** Test coverage for CheckContentAsync focusing on production failure modes (silent-fail paths):
- **NoOpPurviewAuditService (2 tests):** Always returns IsBlocked=false, handles null/empty/whitespace content
- **PurviewAuditService resilience (5 tests):** blockEnabled=false (immediate return), Graph unavailable (silent fail), pre-cancelled token (silent fail), null/empty/whitespace content (silent fail), clientDisplayName provided (completes)
- **Skip stubs (4 tests):** Graph returns ShouldBlock=true/ShouldProcess=false — blocked by no IPurviewGraphClient injection seam  
**Rationale:** Silent-fail paths are production failure modes (when Purview down/unavailable, API must degrade gracefully). These are testable now without refactoring. Happy-path Graph integration (ShouldBlock=true → IsBlocked=true) is important but not failure-critical; skip stubs document exactly what's needed when someone adds IPurviewGraphClient interface.  
**Test Results:** Baseline 210 tests → 225 tests (11 new, all passing; 4 skipped). 221/225 pass, 4 documented skips.  
**Why:** Production resilience testing first; Graph integration tests unblock when IPurviewGraphClient interface is added.

### 2026-04-11T00:00:00Z: Purview Content Check — Synchronous Precheck DLP Endpoint (IMPLEMENTED)
**By:** Freamon (Backend Dev)  
**Status:** Implemented  
**What:** Added synchronous DLP content-check capability. New POST /api/content-check/{clientAppId}/{tenantId} endpoint evaluates the prompt against Purview DLP policy BEFORE forwarding to OpenAI. Returns HTTP 451 (Unavailable For Legal Reasons) if content should be blocked.  
**Components:**
- CheckContentAsync interface method (synchronous DLP evaluation)
- PurviewContentCheckResult record (carries blocking verdict and optional message)
- POST /api/content-check endpoint receiving raw prompt, looking up client display name, calling CheckContentAsync, returning 451 if blocked
- PurviewAuditService implementation: 5-second timeout, fail-open design (all exceptions caught and logged, returns IsBlocked=false)
- NoOpPurviewAuditService: stub returning IsBlocked=false  
**Design:**
- Timeout: 5-second timeout prevents slow Purview from blocking hot path
- Error handling: ALL exceptions (network, auth, timeout, Graph API errors) caught and logged at Warning level, returns IsBlocked=false (fail-open)
- Graph API flow: build PurviewSettings, create PurviewGraphClient, GetProtectionScopesAsync, ProcessContentAsync if needed, return verdict
- Client lookup: uses IRepository<ClientPlanAssignment> for DisplayName, falls back to clientAppId if not found (never blocks on missing record)
- Status code: 451 (Unavailable For Legal Reasons) is conventional for content filtering  
**Why:** APIM needs a synchronous decision point to block prompts BEFORE they reach the LLM. Existing EmitAuditEventAsync runs asynchronously in background and cannot provide blocking verdict in time.

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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
