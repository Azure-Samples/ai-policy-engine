# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `src/Chargeback.Api/` — .NET backend API (my primary workspace)
- `src/Chargeback.AppHost/` — Aspire orchestration
- `src/Chargeback.ServiceDefaults/` — Shared service configuration
- `src/Directory.Packages.props` — Central package management

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Phase 0 — Storage Migration (2026-03-31)

**What was done:** Implemented full Phase 0 from architecture-v2. CosmosDB is now source of truth for all configuration data (plans, clients, pricing, usage policy). Redis is cache-only with write-through pattern.

**Key files created:**
- `Services/IRepository.cs` — generic repository interface (`IRepository<T>`)
- `Services/CosmosRepositoryBase.cs` — shared Cosmos CRUD base class
- `Services/CosmosPlanRepository.cs`, `CosmosClientRepository.cs`, `CosmosPricingRepository.cs`, `CosmosUsagePolicyRepository.cs` — concrete Cosmos repos
- `Services/CachedRepository.cs` — write-through Redis cache decorator
- `Services/ConfigurationContainerProvider.cs` — Cosmos "configuration" container initialization
- `Services/RedisToCosmossMigrationService.cs` — Redis→Cosmos data migration (IHostedService)
- `Services/CacheWarmingService.cs` — Cosmos→Redis cache warming (IHostedService)

**Key files modified:**
- `Models/PlanData.cs`, `ClientPlanAssignment.cs`, `ModelPricing.cs`, `UsagePolicySettings.cs` — added `Id` and `PartitionKey` for Cosmos
- `Services/IUsagePolicyStore.cs` + `UsagePolicyStore.cs` — refactored to use `IRepository<UsagePolicySettings>` internally
- `Services/LogDataService.cs` — updated for new `IUsagePolicyStore` interface
- `Program.cs` — full DI wiring: Cosmos repos → CachedRepository wrappers → hosted services
- All 6 endpoint files refactored to use `IRepository<T>` instead of direct Redis

**Patterns established:**
- Single Cosmos container "configuration" with partition key `/partitionKey` for all config entities
- Partition values: "plan", "client", "pricing", "settings"
- Write path: endpoint → `IRepository<T>` → `CachedRepository.UpsertAsync` → Cosmos first → Redis cache
- Read path: endpoint → `IRepository<T>` → `CachedRepository.GetAsync` → Redis first → Cosmos fallback
- Startup order: `RedisToCosmossMigrationService` → `CacheWarmingService` → app ready
- Ephemeral data (rate limits, logs, traces, locks) stays Redis-only
- Test fixture uses `RedisBackedRepository<T>` to preserve FakeRedis seeding patterns

**Decisions:**
- `GetAllAsync` always queries Cosmos (source of truth for listings), not Redis scan
- Repository classes made `public` (not internal) for test fixture accessibility
- Corrupted Redis data returns null from repository (treated as "not found"), not 500

### Phase 1 — Foundation: New Models + CRUD (2026-03-31)

**What was done:** Implemented all 10 work items (F1.1–F1.10) for Phase 1. Added routing policy entity with full CRUD, extended existing models with multiplier billing and request-based quota fields, and wired everything into the repository/DI/cache-warming pipeline.

**Key files created:**
- `Models/ModelRoutingPolicy.cs` — routing policy entity (Id, Name, Rules, DefaultBehavior, FallbackDeployment)
- `Models/RouteRule.cs` — individual route rule (RequestedDeployment → RoutedDeployment, Priority, Enabled)
- `Models/RoutingBehavior.cs` — enum (Passthrough, Deny)
- `Services/CosmosRoutingPolicyRepository.cs` — Cosmos persistence, partition key "routing-policy"
- `Endpoints/RoutingPolicyEndpoints.cs` — full CRUD with deployment validation against DeploymentDiscoveryService

**Key files modified:**
- `Models/ModelPricing.cs` — added Multiplier (decimal, default 1.0m), TierName (string, default "Standard")
- `Models/PlanData.cs` — added ModelRoutingPolicyId, MonthlyRequestQuota, OverageRatePerRequest, UseMultiplierBilling
- `Models/ClientPlanAssignment.cs` — added ModelRoutingPolicyOverride, CurrentPeriodRequests, OverbilledRequests, RequestsByTier
- `Models/ClientUsageResponse.cs` — added request usage fields + RequestUtilizationPercent
- `Models/PlanCreateRequest.cs`, `PlanUpdateRequest.cs` — added new plan fields to DTOs
- `Models/ModelPricingCreateRequest.cs` — added Multiplier, TierName
- `Services/RedisKeys.cs` — added RoutingPolicy key, RoutingPolicyPrefix, deployment-scoped rate limit keys
- `Services/CacheWarmingService.cs` — warms routing policy cache on startup
- `Services/RoutingPolicyValidator.cs` — fixed property name (TargetDeployment → RoutedDeployment)
- `Endpoints/PricingEndpoints.cs` — updated seed data with multiplier/tier values, upsert handler passes through new fields
- `Endpoints/PlanEndpoints.cs` — create/update wire new fields, billing period reset includes request counters
- `Endpoints/ClientDetailEndpoints.cs` — response includes request usage data + utilization %
- `Program.cs` — DI registration for CosmosRoutingPolicyRepository + CachedRepository<ModelRoutingPolicy>, endpoint mapping

**Patterns followed:**
- Same repository pattern as Phase 0: CosmosRoutingPolicyRepository → CachedRepository<ModelRoutingPolicy> wrapper
- Partition key "routing-policy" in shared "configuration" container
- All new fields have safe defaults (0, false, null, empty) — existing data won't break
- Routing policy delete returns 409 if policy is referenced by any plan or client assignment
- Deployment validation skipped when discovery returns empty (service may be unconfigured)
- Seed pricing multipliers: GPT-4.1=1.0x Standard, GPT-4.1-mini=0.33x Standard, GPT-5.2=3.0x Premium

**Test results:** 129/129 tests pass, 0 regressions

### Phase 1 — Model Routing + Per-Request Multiplier Pricing (2026-03-31)

**What was done:** Implemented all 10 work items (F1.1–F1.10) for Phase 1. Added routing policy entity with full CRUD endpoints, extended models with multiplier billing and request-based quota, and integrated with repository/DI/cache-warming.

**Key files created:**
- `Models/ModelRoutingPolicy.cs` — routing policy with rules and behaviors
- `Models/RouteRule.cs`, `RoutingBehavior.cs` — routing rule and behavior types
- `Repositories/CosmosRoutingPolicyRepository.cs` — Cosmos persistence
- `Endpoints/RoutingPolicyEndpoints.cs` — GET/POST/PUT/DELETE with validation

**Key files extended:**
- `Models/ModelPricing.cs` — Multiplier (default 1.0m), TierName
- `Models/PlanData.cs` — ModelRoutingPolicyId, MonthlyRequestQuota, UseMultiplierBilling
- `Models/ClientPlanAssignment.cs` — ModelRoutingPolicyOverride, request usage tracking
- `Services/CacheWarmingService.cs` — routing policy cache warmup
- `Program.cs` — DI registration for routing repository

**Patterns established:**
- Routing uses exact Foundry deployment matching (no glob/regex)
- Three routing modes: per-account, enforced, QoS-based (via DefaultBehavior + rules)
- Multiplier pricing: cost = 1 × model_multiplier (e.g., GPT-4.1-mini = 0.33x baseline)
- All new fields have safe defaults (backward compatible)
- Routing policy delete enforces referential integrity
- Deployment validation against DeploymentDiscoveryService with graceful degradation

**Test results:** 129/129 tests maintained, awaiting Bunk Phase 1 test pass

### Phase 2 — Enforcement: Precheck + Calculator + Log Ingest (2026-03-31)

**What was done:** Implemented all 7 work items (F2.1–F2.7). Routing evaluation in the precheck hot path, deployment-scoped rate limits, multiplier billing in log ingest, extended audit/billing documents, and two new export endpoints.

**Key files created:**
- `Models/RequestSummaryResponse.cs` — response DTOs for request-summary endpoint
- `Endpoints/RequestBillingEndpoints.cs` — GET /api/chargeback/request-summary + GET /api/export/request-billing

**Key files modified:**
- `Endpoints/PrecheckEndpoints.cs` — routing evaluation via RoutingEvaluator, in-memory policy cache (30s TTL), deployment-scoped rate limit keys, AllowedDeployments check on routed deployment, enriched response with routedDeployment/requestedDeployment/routingPolicyId
- `Endpoints/LogIngestEndpoints.cs` — multiplier billing (effectiveRequestCost, tier tracking, overage), request counter updates on ClientPlanAssignment, billing period reset includes request counters, routing metadata in audit items
- `Services/ChargebackCalculator.cs` — added GetTierName() and GetMultiplier() public methods
- `Services/IChargebackCalculator.cs` — interface extended with GetTierName and GetMultiplier
- `Models/AuditLogDocument.cs` — added RequestedDeploymentId, RoutingPolicyId, Multiplier, EffectiveRequestCost, TierName (all nullable)
- `Models/BillingSummaryDocument.cs` — added TotalEffectiveRequests, EffectiveRequestsByTier, MultiplierOverageCost (all nullable)
- `Models/AuditLogItem.cs` — added routing/multiplier fields for channel transport
- `Services/AuditLogWriter.cs` — passes through new fields to AuditLogDocument
- `Services/AuditStore.cs` — accumulates multiplier billing fields in billing summary upserts
- `Program.cs` — maps RequestBillingEndpoints

**Test factory updated:**
- `ChargebackApiFactory.cs` — registers IRepository<ModelRoutingPolicy> with RedisBackedRepository

**Patterns followed:**
- PrecheckEndpoints uses ConcurrentDictionary in-memory cache for routing policies (30s refresh), not Redis per-request
- RoutingEvaluator is pure static logic — adopted Bunk's existing implementation unchanged
- Rate limit keys use deployment-scoped overload when deployment is available, fall back to legacy keys for backward compat
- All new AuditLogDocument/BillingSummaryDocument fields are nullable — existing data stays valid
- Multiplier billing only activates when plan.UseMultiplierBilling is true
- Routing policy resolution: ClientPlanAssignment.ModelRoutingPolicyOverride ?? PlanData.ModelRoutingPolicyId
- AllowedDeployments check runs on the ROUTED deployment, not the originally requested one

**Test results:** 200/200 tests pass, 0 regressions
