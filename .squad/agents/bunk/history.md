# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `src/Chargeback.Tests/` — xUnit test suite (my primary workspace)
- `src/Chargeback.Benchmarks/` — Performance benchmarks
- `src/Chargeback.LoadTest/` — Load test scenarios

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-31: B5.1 + B5.2 — Phase 0 Storage Migration Tests

**What:** Wrote 36 unit tests across 3 test files for the Phase 0 storage migration architecture:
- `src/Chargeback.Tests/Repositories/CachedRepositoryTests.cs` — 16 tests covering cache hit, cache miss, write-through, delete, eviction recovery, Cosmos failure, Redis failure, null handling, cancellation
- `src/Chargeback.Tests/Repositories/CacheWarmingServiceTests.cs` — 10 tests covering happy path, Redis unavailable (logs warning, doesn't fail), Cosmos unavailable (fails startup), empty state, cancellation
- `src/Chargeback.Tests/Repositories/RedisToCosmossMigrationServiceTests.cs` — 10 tests covering migration, idempotency, skip-if-exists, error resilience, empty state, cancellation

**Also created production contracts (for Freamon to adopt/adjust):**
- `src/Chargeback.Api/Repositories/IRepository.cs` — generic repository interface
- `src/Chargeback.Api/Repositories/CachedRepository.cs` — write-through cache implementation
- `src/Chargeback.Api/Repositories/CacheWarmingService.cs` — Cosmos → Redis cache warming hosted service
- `src/Chargeback.Api/Repositories/RedisToCosmossMigrationService.cs` — Redis → Cosmos migration hosted service

**Edge cases tested:**
- Redis failure on read → graceful Cosmos fallback (no exception to caller)
- Redis failure on write → Cosmos persists, data safe (Redis cache stale but recoverable)
- Cosmos failure on write → exception propagates (no silent data loss)
- Null/missing entity → returns null, does NOT cache null in Redis
- Eviction recovery → transparent cache rebuild on next read
- Migration idempotency → safe to run on every startup
- CacheWarming: Redis down → log warning, don't block startup; Cosmos down → fail startup

**Test patterns:**
- Use `FakeRedis` for in-memory Redis simulation (existing project pattern)
- Use NSubstitute for mocking interfaces (`IRepository<T>`, `ICacheWarmable`, `IMigratable`)
- Verify Redis state via `FakeRedis.Database.StringGetAsync()` instead of `Received()` on `StringSetAsync` — avoids overload resolution issues with NSubstitute
- Tests use `TestEntity` (simple Id+Name) to avoid coupling to production models
- Namespace: `Chargeback.Tests.Repositories`

**Baseline:** 93 tests before → 129 tests after (all passing)

### 2026-03-31: B5.3–B5.6 — Phase 1 Routing & Multiplier Pricing Tests

**What:** Wrote 41 unit tests across 4 test files for Phase 1 features (proactive — tests written from architecture spec while Freamon builds production code):

- `src/Chargeback.Tests/Routing/RoutingEvaluatorTests.cs` — 13 tests: exact match, no match, priority ordering, disabled rules, Passthrough/Deny default behaviors, empty rules, null policy, fallback deployment, multiple rules, all-disabled-falls-to-default
- `src/Chargeback.Tests/Pricing/EffectiveRequestCostTests.cs` — 9 tests: baseline/cheap/premium multipliers via [Theory], zero multiplier → 1.0, negative multiplier → 1.0, unknown deployment → 1.0, model name fallback, empty cache, deploymentId-first priority
- `src/Chargeback.Tests/Pricing/MultiplierOverageCostTests.cs` — 11 tests: billing disabled → 0, unlimited quota → 0, within quota [Theory], at boundary, over quota, partial overage (straddles boundary), already over quota, premium model overage, cheap model fractional overage
- `src/Chargeback.Tests/Routing/RoutingPolicyValidationTests.cs` — 8 tests: valid deployment passes, invalid deployment fails, fallback must be known, valid fallback passes, mixed valid/invalid rejects whole policy, empty Foundry list fails all, multiple invalid reports all errors, case-insensitive matching

**Also created production contracts (for Freamon to adopt/adjust):**
- `src/Chargeback.Api/Services/RoutingEvaluator.cs` — static routing evaluation (pure logic, no dependencies)
- `src/Chargeback.Api/Services/RoutingPolicyValidator.cs` — validates routing rules against Foundry deployments
- Extended `IChargebackCalculator` and `ChargebackCalculator` with `CalculateEffectiveRequestCost()` and `CalculateMultiplierOverageCost()`
- Added test constructor to `ChargebackCalculator` for pre-seeding pricing cache

**Key design decisions validated by tests:**
- Routing uses exact Foundry deployment match — no glob/regex (per Zack Way decision)
- Zero/negative multipliers default to 1.0 (safe default, not 0 which would make requests free)
- Overage is capped at effectiveCost per request (can't overage more than the request itself)
- At quota boundary (exactly at limit), no overage — boundary is inclusive
- One bad deployment in a routing policy rejects the entire policy (atomic validation)
- RoutingPolicyValidator is strict: empty Foundry deployment list fails all rules (endpoint implementation currently skips validation when empty — noted discrepancy)

**Discrepancy found:** RoutingPolicyEndpoints.ValidateDeployments skips validation when knownIds.Count == 0 (line 245). RoutingPolicyValidator (and the spec) says empty Foundry list should fail all. This should be discussed with Freamon/team.

**Test patterns:**
- `RoutingEvaluator` is a static class — tests exercise pure functions directly, no mocking needed
- `RoutingPolicyValidator` uses NSubstitute mock of `IDeploymentDiscoveryService`
- `ChargebackCalculator` tests use new `ChargebackCalculator(Dictionary<string, ModelPricing>)` constructor for seeded pricing cache
- Freamon's model property names: `RouteRule.RequestedDeployment`/`RoutedDeployment`, enum `RoutingBehavior` (not `DefaultRoutingBehavior`)

**Baseline:** 129 tests before → 170 tests after (all passing)

### 2026-03-31: B5.3–B5.6 — Phase 1 Routing & Multiplier Pricing Tests

**What:** Wrote 41 unit tests across 4 test files for Phase 1 features (routing evaluation, multiplier cost calculation, and validation):

- `src/Chargeback.Tests/Routing/RoutingEvaluatorTests.cs` — 13 tests: exact match, no match, priority ordering, disabled rules, Passthrough/Deny default behaviors, empty rules, null policy, fallback deployment
- `src/Chargeback.Tests/Pricing/EffectiveRequestCostTests.cs` — 9 tests: baseline/cheap/premium multipliers via [Theory], zero/negative → 1.0 default, unknown deployment → 1.0, model name fallback, empty cache
- `src/Chargeback.Tests/Pricing/MultiplierOverageCostTests.cs` — 11 tests: billing disabled → 0, unlimited quota → 0, within quota, at boundary (inclusive), over quota, partial overage, already over, premium/cheap models
- `src/Chargeback.Tests/Routing/RoutingPolicyValidationTests.cs` — 8 tests: valid deployment passes, invalid fails, fallback validation, atomicity (one bad → reject all), empty Foundry (strict), case-insensitive matching

**Also created production contracts (for Freamon to adopt/adjust):**
- `src/Chargeback.Api/Services/RoutingEvaluator.cs` — static routing evaluation (pure logic)
- `src/Chargeback.Api/Services/RoutingPolicyValidator.cs` — validates routing rules against Foundry deployments
- Extended `IChargebackCalculator` and `ChargebackCalculator` with:
  - `CalculateEffectiveRequestCost(string deploymentId, string? modelName)` → decimal (1.0x baseline, 0.33x cheap, 3.0x premium)
  - `CalculateMultiplierOverageCost(decimal monthlyRequestQuota, decimal usageCount, decimal multiplier)` → decimal (0 if unlimited/disabled/within quota, charged per excess)

**Key design decisions validated:**
- Routing uses exact Foundry deployment match — no glob/regex (Zack Way decision)
- Zero/negative multipliers default to 1.0 (safe default, not 0)
- Overage is capped at effectiveCost per request
- Quota boundary is inclusive (at limit exactly, no overage)
- One bad deployment in routing policy rejects the entire policy (atomic validation)
- Empty Foundry deployment list fails all rules (strict validation)

**Discrepancy flagged:** RoutingPolicyEndpoints.ValidateDeployments skips validation when knownIds.Count == 0 (line 245). RoutingPolicyValidator (and spec) says empty Foundry list should fail all. Decision: strict rejection approved by user for Phase 2.

**Test patterns:**
- `RoutingEvaluator` is static — tests exercise pure functions directly, no mocking
- `RoutingPolicyValidator` uses NSubstitute mock of `IDeploymentDiscoveryService`
- `ChargebackCalculator` tests use new `ChargebackCalculator(Dictionary<string, ModelPricing>)` constructor for seeded pricing cache
- All tests use [Theory] for data-driven scenarios

**Baseline:** 129 tests before → 170 tests after (all passing)
