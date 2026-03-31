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
