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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
