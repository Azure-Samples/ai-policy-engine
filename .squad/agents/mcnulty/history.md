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

### 2026-03-31 — Re-Review Complete: All 11 Findings Verified ✅

**Status:** APPROVED — independent verification of all 11 fixes.

McNulty re-reviewed every file touched by Freamon (6 backend) and Kima (5 frontend). All fixes are correctly implemented, no regressions, no new issues introduced.

**Key Verification Points:**
- B1: Multiplier request quota enforcement is in the right place in PrecheckEndpoints.cs (after token check, before rate limits). Returns 429 with correct field names.
- B2: `billingPeriod` is `string` in types.ts. RequestBilling.tsx renders it as-is — no `.year`/`.month` access.
- B3: `RouteRule` has `priority: number` + `enabled: boolean`. RoutingPolicies.tsx form has both editable priority input and enabled toggle.
- S1: `Repositories/` directory confirmed deleted. Zero namespace references remain.
- S2: Both APIM policies use `JObject` construction — zero string interpolation in outbound log body.
- S3: `SemaphoreSlim(1,1)` with proper double-check locking. No `volatile bool`.
- S4: `routingPolicyId` flows end-to-end: precheck response → APIM capture → log payload → `LogIngestRequest` → `AuditLogItem` → `AuditLogDocument`.
- S5: `ModelPricing` has `multiplier` + `tierName` on the base type. No `ModelPricingExtended` band-aid.
- S6: `PlanCreateRequest` and `PlanUpdateRequest` both include all 4 multiplier billing fields.
- S7: `lock(_cacheLock)` with double-check pattern. Timestamp set inside lock, Redis read outside. No bare access.
- S8: `parseErrorMessage` helper applied consistently to all 27 API functions.

**Verdict:** `.squad/decisions/inbox/mcnulty-rereview-verdict.md`

### 2026-04-01 — Full Codebase Review: Pre-Ship Quality Audit

**Status:** CONDITIONALLY APPROVED for preview. Fix CRITICALs before GA.

**Scope:** Entire product — every file in every layer. Not a diff review; a full product audit requested by Zack Way before shipping.

**Findings: 47 total (11 CRITICAL, 20 IMPORTANT, 16 IMPROVEMENT)**

**Critical Issues Found:**
1. **CORS allows any origin** (Program.cs:136) — exploitable with JWT auth
2. **AuditStore initialization race** — volatile bool without semaphore, unlike ConfigurationContainerProvider which was correctly fixed
3. **APIM Contributor on entire RG** (main.bicep:322) — can delete any resource
4. **HTTP allowed on Chargeback API** (apimFuncApi.bicep:34) — cleartext JWT tokens
5. **No subscription requirement** on Chargeback API in APIM (apimFuncApi.bicep:37)
6. **Cosmos local auth enabled** (cosmosAccount.bicep:15) — keys bypass RBAC
7. **ACR credentials in plain secret** (containerApp.bicep:85-97) — not Key Vault
8. **APIM on-error leaks internals** — ErrorSource, ErrorPolicyId headers exposed
9. **JWT validation accepts any tenant** — no issuer/scope restriction
10. **Frontend type mismatches** — RequestSummaryResponse field names don't match backend
11. **Health checks only in Development** — production Container App has no /health

**Key Patterns Found Across Layers:**
- Infrastructure Bicep modules have inconsistent security posture: Redis is excellent (TLS, Entra-only), but Key Vault uses legacy access policies, Storage allows public blobs, Cosmos has keys enabled
- Thread safety was correctly fixed in ChargebackCalculator (lock + double-check) and ConfigurationContainerProvider (SemaphoreSlim), but AuditStore was missed
- Frontend types drifted from backend after multiplier billing feature — several interfaces have wrong field names
- Test coverage has gaps in newer endpoint groups (RoutingPolicy, Deployment, RequestBilling CRUD endpoints)
- Documentation has wrong repo URLs in 3 files and contradictory TTL values

**What's Solid:**
- Repository pattern, write-through caching, routing evaluator, billing math, authorization model, Redis security, managed identity usage, Aspire orchestration, 198-test suite, backward compatibility

**Review output:** `.squad/decisions/inbox/mcnulty-full-codebase-review.md`

### 2026-04-01 — Product Rebrand: README & Documentation Rewrite

**Status:** COMPLETE ✅

McNulty rewrote README.md and updated all associated documentation to reflect the new product identity and all features built during Phases 0–4.

**What Changed:**

1. **Product Renaming**: "Azure API Management OpenAI Chargeback Environment" → **"Azure AI Gateway Policy Engine"**
   - Emphasizes APIM-based AAA (Authentication, Authorization, Accounting) for AI workloads
   - Telecom/RADIUS heritage positioning
   - Reflects the policy engine architecture (not just chargeback)

2. **README.md Completely Rewritten** (590 lines):
   - TL;DR clarifies the three pillars: durability (CosmosDB source of truth), routing (auto-router), and billing (multiplier pricing)
   - New "The Problem We Solve" table: 9 challenges with solutions addressing new features
   - Expanded Architecture section with detailed decision flow (precheck → routing → rate limit → cost)
   - **New Key Features Section** (subsections):
     - 🔐 Authentication & Authorization at the Gate
     - 🚀 Intelligent Model Routing (Auto-Router) with three modes
     - 💰 Per-Request Multiplier Pricing (GHCP-style) with examples
     - 🗄️ CosmosDB Source of Truth + Redis Cache (write-through pattern)
     - 📊 Adaptive Billing Dashboard (token/multiplier/hybrid modes)
     - 📋 Bill-Back Reporting (per-client, tier breakdown, CSV export)
     - ⚡ APIM Policy Enforcement at the Gateway
     - 🧪 Comprehensive Test Suite (198+ tests)
     - 🏗️ Production-Ready Infrastructure
   - Updated Dashboard section with routing policies page
   - New API Endpoints table (18 endpoints including routing, billing, export)
   - Added note about internal "Chargeback" naming and pending rename

3. **Documentation Updates**:
   - `docs/ARCHITECTURE.md`: Updated product name, added CosmosDB architecture, detailed request flow with routing & multiplier billing decisions
   - `docs/DOTNET_DEPLOYMENT_GUIDE.md`: .NET 9 (was 10), product name updated
   - `docs/FAQ.md`: Completely rewritten (7 sections, 30+ Q&A covering all new features):
     - Multiplier pricing examples
     - Auto-router behavior vs. enforced rewriting
     - CosmosDB durability guarantees
     - Multi-tenant scenarios
     - Hybrid billing mode
     - Deployment options (Bicep vs. Terraform)
     - Troubleshooting common issues
   - `docs/USAGE_EXAMPLES.md`: Updated product name, examples remain valid

4. **TL;DR Messaging**:
   - Before: "Usage tracking and chargeback through APIM"
   - After: "APIM-based AAA for AI workloads" with focus on durability, routing, and adaptive billing

**Files Modified**:
- README.md (major rewrite, 590 lines)
- docs/ARCHITECTURE.md (product name, CosmosDB architecture, detailed flow)
- docs/DOTNET_DEPLOYMENT_GUIDE.md (.NET 9, product name)
- docs/FAQ.md (comprehensive rewrite, 7 sections)
- docs/USAGE_EXAMPLES.md (product name)

**Key Messaging Retained**:
- Multi-tenant customer model (clientAppId:tenantId)
- WebSocket real-time dashboard
- 198+ test suite
- Bicep/Terraform dual IaC paths
- Aspire orchestration
- CosmosDB audit trail (36 months)
- Purview integration (optional)

**What's New in Documentation**:
- Explicit CosmosDB source-of-truth architecture (vs. Redis-only perception in old docs)
- Three routing modes explained (per-account, enforced, QoS-based)
- Multiplier pricing with concrete examples (1.0x baseline, 0.33x tier)
- Hybrid billing mode support (token + multiplier mixed plans)
- Bill-back reporting details (per-client effective requests, tier breakdown)
- Adaptive dashboard UI behavior
- Auto-router decision flow (not enforced rewriting)
- Deployment discovery integration (Foundry)
- Comprehensive FAQ with troubleshooting

**Why This Matters**:
- Customers now understand the product's core value: durable policy engine for AI consumption (not just cost tracking)
- Clear distinction between authentication (at gate), authorization (plan + deployment checks), and accounting (billing)
- Documentation reflects actual architecture (CosmosDB + Redis) and not implied Redis-only storage
- New features (multiplier billing, routing, adaptive UI) are front-and-center
- Legacy "Chargeback" naming acknowledged with pending rename roadmap

### 2026-04-01 — Deep Research: Agent365 SDK Integration Architecture

**Status:** PROPOSAL — awaiting Zack Way's review

**Context:** Zack discovered the official **Microsoft Agent 365 SDK** for enterprise observability and identity. Directive: "Each APIM client = an Agent 365 agent. All calls to Foundry endpoints get pushed as observability data through the A365 SDK."

**Key Findings:**

1. **What Agent365 SDK Is:**
   - Enterprise-grade extensions for AI agents: Entra-backed identity, OpenTelemetry observability, governed MCP tool access, agent blueprints
   - **Not** a framework — enhances existing agents built on any SDK (Agent Framework, Semantic Kernel, OpenAI, LangChain, Copilot Studio)
   - NuGet packages: `Microsoft.Agents.A365.Observability*`, `Microsoft.Agents.A365.Runtime*`, `Microsoft.Agents.A365.Tooling`
   - Current version: `0.2.152-beta` (Frontier preview program)

2. **Relationship to Existing Purview Integration:**
   - **CRITICAL FINDING:** A365 Observability SDK is **COMPLEMENTARY, NOT REPLACEMENT** to our `Microsoft.Agents.AI.Purview` DLP integration
   - **Two separate concerns:**
     - `Microsoft.Agents.AI.Purview` = Real-time DLP policy **enforcement** (`processContent` endpoint returns block/allow decision at request time)
     - `Microsoft.Agents.A365.Observability` = Structured **telemetry export** (OpenTelemetry spans sent to M365 admin center / Purview compliance dashboards for audit/visibility)
   - **Analogy:** Purview DLP = TSA checkpoint (blocks contraband), A365 Observability = airport security cameras (records everything)
   - **Both are needed:** Precheck calls `CheckContentAsync` (Purview SDK) to block sensitive prompts, log ingest emits `ExecuteInference` spans (A365 SDK) for audit trail

3. **A365 Observability Data Model:**
   - **Four operation types:** `InvokeAgent` (session start), `ExecuteInference` (LLM call), `ExecuteTool` (function call), `OutputMessages` (response)
   - **BaseData structure:** All DTOs inherit `Name`, `Attributes` (OTel tags), `StartTime`, `EndTime`, `SpanId`, `ParentSpanId`, `TraceId`, `Duration`
   - **Key attributes:** `gen_ai.agent.id`, `gen_ai.agent.name`, `microsoft.tenant.id`, `user.id`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.tool.name`, `threat.diagnostics.summary` (DLP action)
   - **Export:** Batched spans (512/batch, 5s delay) to Agent365 backend, requires token resolver, toggle via `ENABLE_A365_OBSERVABILITY_EXPORTER=true`

4. **Agent Identity Model:**
   - **Current:** Each client is a `ClientPlanAssignment` with `ClientAppId` (Entra service principal, `idtyp=app`)
   - **Agent365:** Agents get Agentic User identities (`idtyp=user` tokens) with mailbox, M365 license, org chart presence
   - **Setup:** `a365 setup` CLI creates Agent Blueprint → Agentic App Instance → Agentic User
   - **Integration question:** Three options:
     - **Option A:** Manual `a365 setup` per-client, store `Agent365UserId` in CosmosDB (simple, not scalable)
     - **Option B:** Programmatic provisioning via Graph API on client creation (requires API research)
     - **Option C:** Lightweight — use `ClientAppId` as `gen_ai.agent.id` attribute, skip Agentic User provisioning (fastest, no governance benefits)

5. **Integration Architecture (Recommended):**
   - **Phase 1 (Additive A365 Observability):** Add SDK alongside existing Purview DLP, zero breaking changes
     - Add packages: `Microsoft.Agents.A365.Observability`, `Microsoft.Agents.A365.Observability.Runtime`, `Microsoft.Agents.A365.Runtime`
     - Register `AddA365Tracing` in `Program.cs`, implement `IAgent365TokenResolver`
     - Wrap `PrecheckEndpoints` with `InvokeAgent` scope (session metadata)
     - Wrap `LogIngestEndpoints` with `ExecuteInference` scope (model, tokens, latency, routing decision)
     - Use `BaggageBuilder` for context propagation (`tenant.id`, `agent.id`, `conversation.id`)
     - Set `threat.diagnostics.summary` attribute when `CheckContentAsync` blocks request
     - Make exporter opt-in via config flag (Frontier preview requirement)
   - **Phase 2 (Agent Identity Provisioning):** Deferred pending Zack's strategy choice (Options A/B/C)
   - **Phase 3 (Deprecate Custom PurviewGraphClient):** Never — A365 Observability doesn't expose DLP enforcement APIs

6. **What We Keep vs. Add:**
   - **✅ KEEP:** All existing Purview DLP integration (`Microsoft.Agents.AI.Purview`, `PurviewGraphClient`, `PurviewAuditService`, `CheckContentAsync`, `EmitAuditEventAsync`)
   - **➕ ADD:** A365 SDK packages, OTel scopes, baggage builder, token resolver, DI registration
   - **🔄 INTEGRATE:** A365 spans include DLP action attributes, Purview `contentActivities` continue to flow (separate channel)

7. **Open Questions for Zack:**
   - **Q1:** Agent identity strategy — full Agentic User provisioning (mailbox, license, blueprint) or lightweight mapping (`ClientAppId` as `agent.id`)?
   - **Q2:** Scope of integration — just precheck + log ingest, or also config CRUD endpoints?
   - **Q3:** Foundry endpoint filtering — all requests or only Foundry deployments?
   - **Q4:** Tenant/subscription requirements — Frontier preview access needed for all clients?
   - **Q5:** Testing strategy — how to test without full Frontier-enabled tenant?

8. **Risk Assessment:**
   - **High:** SDK is beta (0.2.152), API may change; Frontier preview access required
   - **Medium:** Token resolver complexity (per-tenant tokens), performance overhead (OTel spans), dual observability channels
   - **Low:** Package dependency bloat (~2MB), zero breaking changes to existing Purview

**Deliverables:**
- Architecture plan: `.squad/decisions/inbox/mcnulty-agent365-architecture.md` (30KB, 10 sections)
- Covers: SDK purpose, Purview relationship, identity model, integration architecture, migration phases, open questions, risk assessment, file changes, recommendations

**Recommendation:**
- **Go forward with Phase 1** (additive A365 Observability, lightweight identity mapping)
- **Defer Phase 2** pending Zack's decision on identity strategy
- **Total effort:** 4-5 days (2-3 backend, 1 tests, 0.5 docs, 1 staging verification)

**Key Insight for Future Work:**
- Microsoft is building a dual observability model for AI agents: **DLP enforcement** (real-time block/allow) and **telemetry export** (audit trail for compliance dashboards)
- Our middleware architecture naturally aligns: precheck = enforcement gate, log ingest = telemetry sink
- A365 SDK gives us M365 admin center visibility without replacing our existing Purview DLP blocking logic
- Agent identity is the open question — full Entra Agentic Users vs. service principal-based observability
- SDK is in beta but stable enough for integration (0.2.152-beta, 5 packages, 13K total downloads)

---

### 2026-05-14 — Cross-Agent Note: azd Terraform Provider Configuration

**From:** Sydnor (Infra/DevOps)  
**Note:** When using Terraform with Azure Developer CLI (azd), the zure.yaml file must explicitly declare an infra: provider block pointing to the terraform module. If omitted, azd defaults to Bicep and looks for infra/main.bicep, which will fail if Terraform is the actual IaC provider. Example config:

\\\yaml
infra:
  provider: terraform
  module: infra/terraform
\\\

This applies to any project mixing IaC tools or migrating from Bicep to Terraform.

### 2026-05-14 — Cross-Agent Note: Infrastructure Changes Must Be Validated Before Commit

**From:** Zack Way (User directive captured by Scribe)  
**Note:** When fixing infrastructure/deployment errors, **always validate fixes by running the relevant `azd` command** (e.g., `azd provision --preview`, `azd up`) **BEFORE committing**. Do not write commits with unvalidated infrastructure changes. This keeps the commit tree clean of speculative/bad infrastructure history and ensures only known-working fixes enter the codebase.

**Application:** All agents working on infrastructure, deployment, or orchestration. Sydnor validated the Terraform tfvars fix via `azd provision --preview` before the orchestration log was written.
