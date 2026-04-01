# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `src/Chargeback.Api/` — .NET backend API
- `src/chargeback-ui/` — React frontend
- `src/Chargeback.AppHost/` — Aspire orchestration
- `src/Chargeback.Tests/` — xUnit tests
- `src/Chargeback.Benchmarks/` — Performance benchmarks
- `src/Chargeback.LoadTest/` — Load testing
- `src/Chargeback.ServiceDefaults/` — Shared service configuration
- `infra/` — Azure Bicep infrastructure
- `policies/` — APIM policy definitions

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-31 — Phase 0 Complete: CosmosDB Source of Truth + Repository Pattern

**Phase 0 Status:** ✅ COMPLETE (Freamon + Bunk)
- Storage architecture migrated: CosmosDB is now the durable source of truth; Redis is a write-through cache.
- Repository pattern implemented: `IRepository<T>` abstraction with four concrete repositories (`CosmosPlanRepository`, `CosmosClientRepository`, `CosmosPricingRepository`, `CosmosUsagePolicyRepository`).
- `CachedRepository<T>` wrapper enforces write-through semantics (persist to Cosmos first, then update Redis).
- All endpoints refactored to use repositories instead of direct Redis calls.
- Startup migration and cache warming services in place for backward compatibility and performance.
- **Test Results:** 36 new tests written (B5.1–B5.2), 129/129 tests pass, zero regressions.

**What This Means for Phase 1 Onwards:**
- All future work (routing, pricing, policy enhancements) now builds on stable repositories.
- No more Redis-only data — all configuration data is durable.
- `IRepository<T>` is the extension point for new entities (e.g., `CosmosModelRoutingPolicyRepository` for Phase 1).
- Caching is transparent to callers — endpoint logic unchanged, but storage is now production-safe.

**Files:**
- New: `Repositories/` (5 files), `Services/RedisToCosmossMigrationService.cs`, `Services/CacheWarmingService.cs`, `Services/RepositoryServiceExtensions.cs`
- Refactored: All endpoints + `Program.cs` + `ConfigurationContainerInitializer.cs`
- Tests: 3 new test files (CachedRepositoryTests, RedisToCosmossMigrationServiceTests, CacheWarmingServiceTests)

**Architecture v2 Accepted:**
- Decision 1: CosmosDB is source of truth, Redis is cache (Phase 0 — COMPLETE)
- Decision 2: Per-REQUEST multiplier (not per-token) — Phase 2–3 work
- Decision 3: Foundry deployment discovery (no pattern matching) — Phase 1 work
- Decision 4: Rate limits on routed deployment — Phase 1 work

**Next Phase:** Phase 1 (Model Routing) — Freamon will add `CosmosModelRoutingPolicyRepository` + routing logic at precheck; Bunk will add routing tests.

### 2026-03-31 — Full Code Review: Phases 0–4 (All Feature Work)

**Verdict:** CONDITIONALLY APPROVED — 3 blocking, 8 should-fix, 5 nice-to-have.

**Blocking Issues Found:**
1. **Precheck does NOT enforce `MonthlyRequestQuota`** for multiplier billing plans. Only `MonthlyTokenQuota` is checked. Plans with `UseMultiplierBilling = true` have zero quota enforcement at the APIM gate. Fix: add request quota check in `PrecheckEndpoints.cs`.
2. **Frontend `RequestSummaryResponse.billingPeriod`** type is `{ year, month }` but backend returns string `"YYYY-MM"`. Will crash `RequestBilling.tsx` at runtime.
3. **Frontend `RouteRule`** missing `priority` and `enabled` fields. Users cannot set rule priority or disable rules from the UI.

**Key Should-Fix Items:**
- Dead code in `Repositories/` directory (duplicates `Services/` with different behavior) — must delete.
- APIM outbound log body uses string interpolation for JSON — injection risk on claims with quotes.
- `ConfigurationContainerProvider.EnsureInitializedAsync` has a race condition (volatile bool insufficient).
- `LogIngestEndpoints` never persists `RoutingPolicyId` — audit trail gap.
- Frontend TypeScript types don't include multiplier billing fields on base types; relies on `Extended` interfaces and runtime type coercion.
- `ChargebackCalculator` pricing cache is not thread-safe.
- Frontend API error handling discards backend error payloads.

**What's Solid:**
- Repository pattern and write-through cache semantics are correct.
- `RoutingEvaluator` is pure, stateless, and well-tested (priority, enabled, deny, passthrough, fallback).
- Multiplier billing math (`effective_cost = 1 × multiplier`) is correct.
- Authorization model applied consistently.
- 200 tests passing with strong critical-path coverage.
- APIM routing integration (precheck → rewrite URI → deployment-scoped rate limits) works correctly.
- Backward compatibility maintained via nullable fields with defaults.

**Review output:** `.squad/decisions/inbox/mcnulty-code-review-verdict.md`

### 2026-03-31 — Deep Architecture Exploration + Feature Design

**Codebase Architecture:**
- Backend uses **Minimal APIs** (no MVC controllers) — all endpoints in `Endpoints/` directory
- Redis is the **primary runtime store** for plans, clients, pricing, logs, traces, rate limits, usage policy
- CosmosDB stores **audit logs** (`audit-logs` container) and **billing summaries** (`billing-summaries` container), partitioned by `/customerKey`
- `ChargebackCalculator` uses an **in-memory pricing cache** refreshed every 30s from Redis — non-blocking on the request path
- **Precheck endpoint** is the APIM enforcement choke point — checks assignment, plan, quota, rate limits, deployment access
- APIM policies call precheck **inbound**, then log usage **outbound** (fire-and-forget POST to `/api/log`)
- Frontend is **tab-based** (no react-router), state-driven in `App.tsx`, polling for live data (5s/10s intervals)
- Auth: AzureAd JWT bearer with three policies: `ExportPolicy`, `ApimPolicy`, `AdminPolicy`

**Key Extension Points:**
- `PlanData.AllowedDeployments` / `ClientPlanAssignment.AllowedDeployments` — existing deployment access control
- `ModelPricing` in Redis (`pricing:{modelId}`) — already per-model, extend with multiplier/tier
- `PrecheckEndpoints.cs` — routing decisions go here (add `routedDeployment` to response)
- `ChargebackCalculator` — cost calculation, extend with `CalculateBillingUnits()`
- `AuditLogDocument` / `BillingSummaryDocument` — extend with routing + multiplier fields (additive, nullable)
- `RedisKeys.cs` — centralized key patterns, add `routing-policy:{policyId}`

**Architecture Decisions Made:**
- Model Routing: new `ModelRoutingPolicy` entity, Redis-backed, attached to plans via `ModelRoutingPolicyId`
- Multiplier Pricing: extend `ModelPricing` with `Multiplier` + `TierName`, extend `PlanData` with unit quotas
- Both features converge at precheck — routing decides *where*, pricing decides *how much*
- All changes are additive/backward-compatible — `UseMultiplierBilling` flag for gradual migration
- No new storage systems — Redis for runtime config, CosmosDB for audit (existing containers)
- Proposal written to `.squad/decisions/inbox/mcnulty-model-routing-pricing-architecture.md`
- Revised proposal (v2) written to `.squad/decisions/inbox/mcnulty-architecture-v2.md`

### 2026-03-31 — Four Design Decisions (from Zack Way) & Architecture v2

**Decision 1: CosmosDB is Source of Truth, Redis is Cache Only**
- All configuration data (plans, clients, pricing, routing policies, usage policy) MUST persist to CosmosDB. Redis is ONLY a write-through cache.
- Architectural implication: New repository pattern (`IRepository<T>` → Cosmos persistence → `CachedRepository<T>` Redis wrapper). New `configuration` Cosmos container. One-time migration service (Redis → Cosmos on startup). Cache warming service. All endpoint refactoring to use repositories instead of direct Redis calls.
- This is the largest body of work (Phase 0) and must complete before feature work.

**Decision 2: Per-REQUEST Multiplier (not per-token)**
- `effective_cost = 1 × model_multiplier` per request. GPT-4.1 = 1.0x, GPT-4.1-mini = 0.33x.
- Architectural implication: Simpler calculator logic — no token division. `MonthlyRequestQuota` replaces `MonthlyUnitQuota`. `CurrentPeriodRequests` replaces `CurrentPeriodUnits`. All "unit" terminology changed to "effective requests".

**Decision 3: Foundry Deployment Discovery (no pattern matching)**
- Routing maps to specific known deployments from Foundry. No globs, no regex.
- Architectural implication: `RouteRule.RequestedDeployment` is exact match only. All `RoutedDeployment` values validated against `IDeploymentDiscoveryService.GetDeploymentsAsync()` on create/update. Existing `DeploymentDiscoveryService` is the integration point.

**Decision 4: Rate Limits on Routed Deployment**
- RPM/TPM limits apply to the routed deployment (what hits the backend), not the requested model.
- Architectural implication: Rate limit Redis keys include deployment ID. New key pattern: `ratelimit:rpm:{client}:{tenant}:{deploymentId}:{window}`. Precheck evaluates rate limits AFTER routing resolution.

**File Paths:**
- Models: `src/Chargeback.Api/Models/` (PlanData.cs, ClientPlanAssignment.cs, ModelPricing.cs, AuditLogDocument.cs, BillingSummaryDocument.cs)
- Endpoints: `src/Chargeback.Api/Endpoints/` (PrecheckEndpoints.cs, PricingEndpoints.cs, PlanEndpoints.cs, etc.)
- Services: `src/Chargeback.Api/Services/` (ChargebackCalculator.cs, RedisKeys.cs, AuditStore.cs, AuditLogWriter.cs)
- APIM Policies: `policies/subscription-key-policy.xml`, `policies/entra-jwt-policy.xml`
- Frontend types: `src/chargeback-ui/src/types.ts`
- Frontend API client: `src/chargeback-ui/src/api.ts`
- Aspire orchestration: `src/Chargeback.AppHost/AppHost.cs`

### 2026-03-31 — Code Review Complete: All 11 Findings Fixed (APPROVED)

**Status:** COMPLETE ✅

McNulty's comprehensive code review of Phases 0–4 delivered 11 findings:
- **3 Blocking (Critical):** B1 (precheck quota), B2 (type mismatch), B3 (missing fields)
- **8 Should-Fix (Important):** S1–S8 (security, race conditions, type safety, error handling)
- **5 Nice-to-Have (Future):** N1–N5 (minor optimizations)

**All 11 Findings Now Fixed:**
- **Freamon (Backend):** Fixed B1, S1, S2, S3, S4, S7 (6 fixes)
- **Kima (Frontend):** Fixed B2, B3, S5, S6, S8 (5 fixes)

**Test & Build Results:**
- Backend: 198/198 tests pass (0 regressions; -22 from deleted dead tests)
- Frontend: tsc clean, vite build clean, 0 new linting issues
- No architectural changes — all fixes are bug corrections + code cleanup

**Production Readiness:** APPROVED FOR MERGE
- Security: JSON injection fixed, audit trail complete, cache thread-safe
- Reliability: Precheck enforces quotas, race conditions eliminated
- Code Quality: Dead code removed, type safety improved, error messages actionable
- Backward Compatibility: Maintained (all new fields nullable with defaults)

**Next Phase:** Deploy backend + frontend together. Schedule N1–N5 optimizations for future sprint.

**Decision:** Merged review findings into `.squad/decisions.md`. Ready for production deployment.
