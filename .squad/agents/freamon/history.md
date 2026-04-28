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

### Code Review Fixes — McNulty's 6 Findings (2026-03-31)

**What was done:** Implemented 6 fixes from McNulty's code review (B1, S1, S2, S3, S4, S7).

**B1 — Precheck multiplier request quota:**
- `Endpoints/PrecheckEndpoints.cs` — Added multiplier request quota check after existing token quota logic. Returns 429 when `effectiveRequests >= plan.MonthlyRequestQuota` and `!plan.AllowOverbilling`. Only activates when `plan.UseMultiplierBilling` and `plan.MonthlyRequestQuota > 0`.

**S1 — Deleted dead Repositories/ directory:**
- Removed `src/Chargeback.Api/Repositories/` (4 files: `CachedRepository.cs`, `CacheWarmingService.cs`, `IRepository.cs`, `RedisToCosmossMigrationService.cs`). All active code lives in `Services/`.
- Updated test files (`CachedRepositoryTests.cs`, `CosmosPersistenceResilienceTests.cs`) to reference `Chargeback.Api.Services` namespace with corrected constructor params (`redisKeyFromId`/`entityId` instead of `keySelector`/`redisKey`).
- Removed `CacheWarmingServiceTests.cs` and `RedisToCosmossMigrationServiceTests.cs` (tested deleted `ICacheWarmable`/`IMigratable` interfaces).
- Removed 4 integration tests from `CosmosPersistenceResilienceTests` that tested deleted interface-based migration/warming services.

**S2 — APIM JSON injection fix:**
- `policies/entra-jwt-policy.xml`, `policies/subscription-key-policy.xml` — Replaced string interpolation (`$"{{\"tenantId\": \"{tenantId}\"..."`) with `JObject` construction in outbound log body. Eliminates JSON injection from JWT claims or subscription names.

**S3 — ConfigurationContainerProvider race condition:**
- `Services/ConfigurationContainerProvider.cs` — Replaced `volatile bool _initialized` with `SemaphoreSlim(1,1)` double-check locking. Safe under concurrent `EnsureInitializedAsync` calls.

**S4 — Persist RoutingPolicyId in audit trail:**
- Both APIM policy files — Added `routingPolicyId` extraction from precheck response in inbound section. Included in outbound JObject log payload.
- `Models/LogIngestRequest.cs` — Added `RoutingPolicyId` property.
- `Endpoints/LogIngestEndpoints.cs` — Passes `ingestRequest.RoutingPolicyId` to `AuditLogItem` instead of hardcoded `null`.

**S7 — ChargebackCalculator pricing cache thread safety:**
- `Services/ChargebackCalculator.cs` — Added `_cacheLock` object. Double-check locking pattern: outer check without lock, inner check-and-set of `_lastCacheRefresh` inside lock, actual Redis read outside lock (async-safe). Prevents stampede while keeping I/O non-blocking.

### 2026-04-11 — Purview Content Check Implementation Complete

**What was done:** Implemented synchronous DLP content-check capability for APIM precheck phase.

**New Components:**
- `PurviewContentCheckResult` (public record) — carries blocking verdict (`IsBlocked` bool) and optional `BlockMessage`
- `CheckContentAsync` interface method on `IPurviewAuditService` — synchronous DLP evaluation called at request time
- POST `/api/content-check/{clientAppId}/{tenantId}` endpoint — receives raw prompt, looks up client display name, calls `CheckContentAsync`, returns 451 if blocked
- `PurviewAuditService.CheckContentAsync` implementation — 5-second timeout, fail-open design (all exceptions caught and logged, returns `IsBlocked=false`)
- `NoOpPurviewAuditService.CheckContentAsync` — stub returning `IsBlocked=false`

**Key Design Decisions:**
- **Fail-open:** If `_blockEnabled=false`, return `IsBlocked=false` immediately without calling Graph API
- **Fail-open on error:** Any exception (auth, network, timeout, Graph API failure) logged at Warning level, returns `IsBlocked=false` — the request MUST proceed even if Purview is down
- **5-second timeout:** Hard limit via `CancellationTokenSource` to prevent slow Purview from blocking hot path
- **Synchronous Graph calls:** Unlike `EmitAuditEventAsync` (async in background), `CheckContentAsync` awaits Graph API calls synchronously because APIM needs the blocking verdict before proceeding
- **Status code 451:** HTTP standard (Unavailable For Legal Reasons) for content filtering/blocking
- **Client lookup:** Uses `IRepository<ClientPlanAssignment>` to fetch `DisplayName`. Falls back to `clientAppId` if assignment not found or DisplayName is null

**Architectural Pattern:**
- Graph API flow: build `PurviewSettings(clientDisplayName)` → create `PurviewGraphClient` → `GetTokenInfoAsync` (resolve userId/tenantId) → `GetProtectionScopesAsync` for UploadText activity → if `ShouldProcess=true` then `ProcessContentAsync` → return verdict
- Error handling: catch and log all exception types (PurviewAuthenticationException, PurviewRateLimitException, timeout, etc.), always return `IsBlocked=false`

**Files Modified:**
- `src/Chargeback.Api/Services/PurviewModels.cs` — added `PurviewContentCheckResult` record
- `src/Chargeback.Api/Services/IPurviewAuditService.cs` — added `CheckContentAsync` method
- `src/Chargeback.Api/Services/PurviewAuditService.cs` — implemented `CheckContentAsync` with timeout and error handling
- `src/Chargeback.Api/Services/NoOpPurviewAuditService.cs` — added stub `CheckContentAsync`
- `src/Chargeback.Api/Endpoints/PrecheckEndpoints.cs` — added POST `/api/content-check/{clientAppId}/{tenantId}` endpoint

**Test Results:** 198 backend tests pass (no regressions). Build clean.

**Next Step:** Sydnor (APIM specialist) to wire POST `/api/content-check` endpoint into APIM policies at request time (inbound policy phase).

**Test results:** 198/198 tests pass, 0 regressions (net -22 from deleted Repositories tests that tested dead code)

### Codebase Review Fixes — 4 Validated Findings (2026-04-01)

**What was done:** Implemented 4 fixes from codebase review (#1, #2, #11, #15).

**#1 — Audit record duplication on retry (CRITICAL):**
- `Services/AuditLogWriter.cs` — Replaced random `Guid.NewGuid()` IDs with deterministic SHA256-based IDs derived from `clientAppId|tenantId|deploymentId|timestamp|totalTokens|promptTokens`. Documents are built once before the retry loop with stable IDs, so retries are idempotent.
- `Services/AuditStore.cs` — Changed `WriteBatchAsync` from `CreateItemAsync` to `UpsertItemAsync`, ensuring partial-success retries don't fail with 409 Conflict on already-written documents.

**#2 — Billing summary race condition (CRITICAL):**
- `Services/AuditStore.cs` — `UpsertBillingSummariesAsync` now uses Cosmos optimistic concurrency with ETags. Reads capture the ETag, writes pass `IfMatchEtag`. On 412 (Precondition Failed), re-reads and retries up to 5 times. Prevents concurrent batches from silently overwriting each other's usage accumulations.

**#11 — AuditStore initialization race condition (IMPORTANT):**
- `Services/AuditStore.cs` — Replaced `volatile bool _initialized` with `SemaphoreSlim(1,1)` double-check locking pattern, matching the fix already applied to `ConfigurationContainerProvider`. Prevents duplicate container creation calls under concurrent initialization.

**#15 — LogIngest lock released before Cosmos write (IMPORTANT):**
- `Endpoints/LogIngestEndpoints.cs` — Increased Redis lock TTL from 5s to 30s to prevent lock auto-expiry during the read-compute-write cycle. Added `LockExtendAsync` call immediately before `clientRepo.UpsertAsync` to refresh the TTL, ensuring the lock cannot expire during Cosmos I/O even if earlier operations were slow.

**Patterns applied:**
- Deterministic document IDs (SHA256 hash of identity fields) for natural idempotency
- ETag-based optimistic concurrency with read-modify-write retry loop for Cosmos upserts
- SemaphoreSlim double-check locking for async initialization (consistent with ConfigurationContainerProvider)
- Redis lock TTL extension before slow I/O operations to prevent silent lock expiry

**Test results:** 198/198 tests pass, 0 regressions

### Routing Policy 400 Bug Fix — Enum Deserialization (2026-04-01)

**What was done:** Fixed silent 400 on routing policy creation. The payload `{"defaultBehavior":"Passthrough"}` was rejected by ASP.NET Core model binding before the endpoint handler ran — no logging, no error detail.

**Root cause:** `RoutingBehavior` enum had no `JsonStringEnumConverter`. System.Text.Json defaults to integer-based enum deserialization. String value `"Passthrough"` failed model binding → framework returned 400 silently. The endpoint handler never executed, so none of our validation logging fired.

**Key files modified:**
- `Models/RoutingBehavior.cs` — Added `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute so the enum serializes/deserializes as strings in any context
- `Services/JsonConfig.cs` — Added `JsonStringEnumConverter` to shared serializer options for explicit serialize/deserialize calls
- `Program.cs` — Added `ConfigureHttpJsonOptions` with `JsonStringEnumConverter` as defense-in-depth for all future enums used in minimal API model binding
- `Endpoints/RoutingPolicyEndpoints.cs` — Added `ILogger` param to `ValidateDeployments` and logging on all rejection paths (empty Foundry, invalid deployment names, missing name). Future 400s will never be silent.

**Patterns established:**
- All enums used in API DTOs must have `[JsonConverter(typeof(JsonStringEnumConverter))]`
- Global `ConfigureHttpJsonOptions` ensures minimal API model binding handles string enums
- Validation methods should accept `ILogger` and log rejection reasons before returning error objects

**Test results:** 198/198 tests pass, 0 regressions

### PR #11 Code Review Fixes — 8 Copilot Findings (2026-04-01)

**What was done:** Fixed 8 legitimate findings from Copilot code review on PR #11.

**Fix 1 — ChargebackCalculator cache refresh timestamp:**
- `Services/ChargebackCalculator.cs` — Moved `_lastCacheRefresh = DateTime.UtcNow` AFTER successful Redis read. Added `_refreshInProgress` volatile flag for stampede prevention while allowing retry on failure.

**Fix 2 — UsagePolicyStore OperationCanceledException:**
- `Services/UsagePolicyStore.cs` — Added `catch (OperationCanceledException) { throw; }` before general catch. Shutdown/cancellation no longer silently falls back to defaults.

**Fix 3 — AuditLogWriter deterministic ID collision:**
- `Models/AuditLogItem.cs` — Added nullable `CorrelationId` property.
- `Models/LogIngestRequest.cs` — Added nullable `CorrelationId` property.
- `Services/AuditLogWriter.cs` — Included `CorrelationId` in SHA256 hash input for deterministic IDs.
- `Endpoints/LogIngestEndpoints.cs` — Flows `CorrelationId` from ingest request to audit item.
- Both APIM policy files — Added `["correlationId"] = context.RequestId.ToString()` to outbound payload.

**Fix 4 — RedisToCosmossMigrationService typo:**
- Renamed file and class from `RedisToCosmossMigrationService` → `RedisToCosmosMigrationService` (removed double-s).
- Updated `Program.cs` and `ChargebackApiFactory.cs` references.

**Fix 5 — APIM JToken.Parse guard:**
- `policies/subscription-key-policy.xml`, `policies/entra-jwt-policy.xml` — Wrapped `JToken.Parse` calls in inline try/catch. On parse failure, falls back to storing raw string value.

**Fix 6 — RoutingPolicyValidator RequestedDeployment validation:**
- `Services/RoutingPolicyValidator.cs` — Now validates `RequestedDeployment` is non-empty and a known Foundry deployment.
- `Endpoints/RoutingPolicyEndpoints.cs` — Same validation in `ValidateDeployments`.
- Updated 2 tests to match new error counts.

**Fix 7 — CachedRepository parallel cache writes:**
- `Services/CachedRepository.cs` — Changed sequential `await TryCacheEntity` loop to `Task.WhenAll` for parallel Redis writes.

**Fix 8 — ChargebackApiFactory hosted service removal:**
- `ChargebackApiFactory.cs` — Changed `ReturnType == typeof(T)` to `ReturnType?.IsAssignableFrom(typeof(T))` for matching factory-registered services.

**Test results:** 198/198 tests pass, 0 regressions

### Phase 3 — PurviewGraphClient: Own Graph REST Client (2026-04-01)

**What was done:** Replaced the `EmitCoreAsync` SDK placeholder with a real implementation
that calls the Microsoft Graph REST API directly. Built because `Microsoft.Agents.AI.Purview`
rc6 keeps all content-processing types (`PurviewClient`, `IScopedContentProcessor`,
`ScopedContentProcessor`) as `internal sealed` — unreachable from outside the assembly.

**Key files created:**
- `Services/PurviewGraphClient.cs` — `internal sealed` class calling three Graph endpoints:
  content activities (audit), processContent (DLP), protectionScopes (scope gate)
- `Services/PurviewModels.cs` — Our own internal DTOs with `JsonPropertyName` for Graph API
  serialization conventions (camelCase + `@odata.type` discriminators on polymorphic fields)

**Key files modified:**
- `Services/PurviewAuditService.cs` — `EmitCoreAsync` fully implemented: decodes JWT claims
  (OID → userId), sends UploadText + DownloadText content activities, evaluates DLP block
  verdict when `blockEnabled=true`; constructor gains `IHttpClientFactory?` param (optional,
  backward-compatible)
- `Services/PurviewServiceExtensions.cs` — Added `services.AddHttpClient("PurviewGraphClient")`
  and passes factory to `PurviewAuditService`
- `Chargeback.Api.csproj` — Added `InternalsVisibleTo("Chargeback.Tests")` for unit test access
- `Chargeback.Tests/PurviewServiceTests.cs` — Updated block verdict test to reflect real behavior;
  implemented two previously-skipped stubs (JWT decoding + @odata.type serialization)

**Patterns established:**
- `PurviewGraphClient` is `internal` — an implementation detail, never in DI
- JWT claims decoded locally: `oid` = userId, `tid` = tenantId, `appid`/`azp` = clientId
- HTTP errors mapped to SDK exception types via `EnsureSuccess()` so the existing retry
  ladder in `EmitWithRetryAsync` handles them correctly
- `HttpClient` lifecycle: injected via `IHttpClientFactory`, not owned by `PurviewGraphClient`
- Per-event `PurviewSettings` (with `AppName = ClientDisplayName`) respected —
  a new `PurviewGraphClient` instance is constructed per event, sharing the pooled HttpClient
- Migration path documented in XML doc comments

**SDK surface facts (rc6):**
- `PurviewRequestException` has `StatusCode` property of type `System.Net.HttpStatusCode`
- `PurviewLocationType` enum values: `Uri`, `Application`, `Domain` (no `Name` variant)
- Exception constructors: `PurviewRequestException` has `ctor(HttpStatusCode, string endpointName)`

**Test results:** 210/212 tests pass (2 skipped — require `IPurviewGraphClient` injection seam), 0 regressions

### Purview Content Check Implementation — Two-Phase DLP (2026-04-11)

**What was done:** Implemented synchronous content-check precheck for Purview DLP blocking. 
This is the first phase of the two-phase flow:
- **Phase 1 (request time):** Check prompt against DLP policy BEFORE forwarding to OpenAI
- **Phase 2 (response time):** Emit audit events AFTER the AI responds (already implemented as `EmitAuditEventAsync`)

**Key files created:**
- New public record `PurviewContentCheckResult` in `Services/PurviewModels.cs` — carries blocking verdict and message
- New POST endpoint `/api/content-check/{clientAppId}/{tenantId}` in `Endpoints/PrecheckEndpoints.cs` — receives raw prompt body from APIM, evaluates against policy, returns 451 (Unavailable For Legal Reasons) if blocked

**Key files modified:**
- `Services/IPurviewAuditService.cs` — Added `CheckContentAsync` interface method for synchronous DLP evaluation
- `Services/PurviewAuditService.cs` — Implemented `CheckContentAsync` with:
  - Immediate fail-open when `_blockEnabled = false`
  - 5-second timeout via `CancellationTokenSource`
  - Synchronous Graph API calls: GetTokenInfoAsync → GetProtectionScopesAsync → ProcessContentAsync
  - Silent-fail on ALL exceptions (catch-all with warning log, returns `{ IsBlocked = false }`)
  - Per-event PurviewSettings construction with `clientDisplayName` as AppName
- `Services/NoOpPurviewAuditService.cs` — Added stub `CheckContentAsync` returning `{ IsBlocked = false }`

**Patterns established:**
- Content-check is fail-open by design — if Purview is down/slow/misconfigured, the request proceeds
- 5-second timeout prevents Purview latency from blocking the precheck hot path
- 451 status code (Unavailable For Legal Reasons) is conventional for content filtering blocks
- Falls back to `clientAppId` as display name when client assignment not found (never blocks on missing client record)
- Blocking message defaults to "Content blocked by policy" when `settings.BlockedPromptMessage` is null

**Test results:** 198/198 tests pass, 0 regressions

### 2026-04-17 — Real Agent365 SDK Scope Integration (Complete)

**What was done:** Replaced all Agent365 Observability stubs with real SDK scope calls using Microsoft.Agents.A365.Observability.Runtime v0.1.75-beta. Full implementation of `InvokeAgentScope.Start()` and `InferenceScope.Start()` with proper parameters. Manual OpenTelemetry configuration added. All scope creation wrapped in fail-safe try/catch blocks.

**Key files modified:**
- `src/Chargeback.Api/Services/Agent365ServiceExtensions.cs` — Removed TODO comment, added manual OpenTelemetry config
- `src/Chargeback.Api/Services/Agent365ObservabilityService.cs` — Implemented real scope creation with InvokeAgentScope and InferenceScope

**Key learnings:**
- Agent365 SDK v0.1.75-beta API differs from documented v1.x versions
- `AddA365Tracing` extension method not available in v0.1.75-beta; manual `AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("Microsoft.Agents.A365.*"))` required
- Namespace conflict: `Azure.Core.Request` vs `Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request` — resolved with alias `using A365Request = ...`
- Placeholder endpoint URI (`https://apim.example.com`) acceptable for APIM scenario without fixed agent endpoint
- Fail-safe design (null returns on exception) allows graceful degradation if observability fails
- Optional parameters (clientDisplayName, correlationId, promptContent) must handle null safely
- SDK API may change in future versions; fail-safe design provides buffer

**Test results:** 235 tests pass (231 pass, 4 documented skips), 0 regressions

**Dependencies resolved:**
- Namespace conflicts handled with explicit aliasing
- All optional SDK parameters safely handled
- Disabled observability (ENABLE_A365_OBSERVABILITY_EXPORTER=false) remains no-op
- Real scope creation verified (IDisposable non-null on success, null on failure)

### Phase 1 — Microsoft Agent365 Observability SDK Integration (2026-04-XX)

**What was done:** Added Agent365 Observability SDK (v0.1.75-beta) alongside existing Purview DLP. Pure additive integration — no replacements. Instrumented precheck and log ingest endpoints with scope-based tracing using manual instrumentation pattern.

**Key files created:**
- Services/Agent365ObservabilityService.cs — service wrapper for A365 scope creation:
  - IAgent365ObservabilityService interface with StartInvokeAgentScope and StartInferenceScope methods
  - Agent365ObservabilityService concrete implementation (currently stub pending SDK API stabilization)
  - NoOpAgent365ObservabilityService for when A365 is disabled
- Services/Agent365ServiceExtensions.cs — DI registration extension:
  - AddAgent365Observability(this IHostApplicationBuilder) extension method
  - Opt-in via ENABLE_A365_OBSERVABILITY_EXPORTER env var (default: false)
  - Registers singleton IAgent365ObservabilityService

**Key files modified:**
- Directory.Packages.props — Added Microsoft.Agents.A365.Observability and Microsoft.Agents.A365.Observability.Runtime at v0.1.75-beta
- Chargeback.Api.csproj — Added package references for A365 SDK
- Program.cs — Called builder.AddAgent365Observability() after builder.AddServiceDefaults()
- Endpoints/PrecheckEndpoints.cs — Instrumented both precheck handlers:
  - Added IAgent365ObservabilityService DI parameter
  - Wrapped handlers with StartInvokeAgentScope using disposal pattern
  - Extracted X-Correlation-ID header for conversation tracking
- Endpoints/LogIngestEndpoints.cs — Instrumented log ingest:
  - Added IAgent365ObservabilityService DI parameter
  - Wrapped log processing with StartInferenceScope after client auth check

**Patterns established:**
- Lightweight identity: Uses ClientAppId as gen_ai.agent.id — no Agentic User provisioning required
- Scope: Precheck + Log Ingest endpoints only (not config CRUD)
- Host tenant scoped: If host has A365 configured via env var, it is on globally (no per-client config)
- Local testing: A365 uses OpenTelemetry natively — visible in Aspire Dashboard
- Exporter: Opt-in via ENABLE_A365_OBSERVABILITY_EXPORTER env var
- Scopes are IDisposable — callers use disposal pattern
- Service methods return null when SDK not configured (callers null-check before use)
- SDK version 0.1.75-beta has limited public API surface — implementation is currently stub with TODO markers

**Decisions:**
- SDK v0.1.75-beta lacks documented scope creation APIs that exist in newer versions (0.2.x+)
- Implemented minimal stub service that compiles and integrates into DI/endpoint flow but does not yet create actual telemetry scopes
- Added TODO comments marking where full implementation will go once SDK stabilizes
- All existing tests (221 passed, 4 skipped) remain green — zero regressions

**Test results:** 221/221 tests pass (4 skipped), 0 regressions

### Phase 5 — A365 Observability Integration Phase 1 (2026-04-17)

**What was done:** Integrated Microsoft Agent365 Observability SDK v0.1.75-beta as additive observability layer. Added `IAgent365ObservabilityService` interface, `Agent365ObservabilityService` stub implementation, and `NoOpAgent365ObservabilityService` no-op fallback. Instrumented Precheck (Precheck + ContentCheck) and LogIngest endpoints with scope-based tracing. Scope creation methods: `StartInvokeAgentScope`, `StartInferenceScope`. Correlation ID extraction from `X-Correlation-ID` header. DI registration via `AddAgent365Observability()` extension with `ENABLE_A365_OBSERVABILITY_EXPORTER` toggle (default: false). Opt-in exporter prevents accidental production enablement.

**Key decisions:**
- Lightweight identity: `ClientAppId` as `gen_ai.agent.id` (no M365 Agentic User provisioning)
- Scope coverage: Precheck + LogIngest hot path only (config CRUD excluded)
- Host-level config: Global toggle via env var (not per-client)
- Local testing via Aspire Dashboard (A365 SDK uses OTel natively)
- Exporter opt-in via env var (dev/test uses OTel-only, production enables A365 when ready)
- SDK v0.1.75-beta (latest on NuGet; newer APIs not yet published)

**Design rationale:**
- Additive: No replacements or breaking changes to existing OpenTelemetry + Purview DLP
- Lightweight: ClientAppId already uniquely identifies clients; no extra provisioning needed
- Focused: Precheck and LogIngest are high-value signal paths; config CRUD is admin operations
- Phased: Env var toggle allows staged rollout and dev/test flexibility
- Future-proof: Service abstraction (`IAgent365ObservabilityService`) supports easy implementation swap once SDK matures

**Implementation notes:**
- `IAgent365ObservabilityService` defined with `StartInvokeAgentScope` and `StartInferenceScope` methods returning `IDisposable?`
- `Agent365ObservabilityService` concrete stub logs scope creation at Trace level but does not create actual spans (TODO markers for SDK maturation)
- `NoOpAgent365ObservabilityService` used when `ENABLE_A365_OBSERVABILITY_EXPORTER` not set
- Integration points: DI registration, PrecheckEndpoints (both Precheck + ContentCheck), LogIngestEndpoints (post-auth IngestLog)
- Correlation ID extracted from `X-Correlation-ID` request header for conversation tracking

**Test results:** 225 tests (221 pass, 4 documented skips), 0 regressions. All existing tests remain green.

**Future work:**
- Monitor SDK releases for v0.2.x+ with stable scope creation APIs
- Replace stub with actual scope implementation once APIs available
- Implement token acquisition for A365 exporter (deferred)
- Add integration tests validating span emission (requires testable SDK APIs)
- Consider enforcing correlation ID (currently optional with GUID fallback)

### Agent365 Observability — Replaced Stubs with Real SDK Calls (2026-04-11)

**What was done:** Replaced all stub implementations in Agent365 observability service with real SDK scope creation calls. The Microsoft.Agents.A365.Observability SDK 0.1.75-beta APIs are now fully integrated.

**Key files modified:**
- `Services/Agent365ServiceExtensions.cs` — Removed TODO comments and stub markers. Added OpenTelemetry configuration with A365 source tracing when enabled. Registers real `Agent365ObservabilityService` when `ENABLE_A365_OBSERVABILITY_EXPORTER=true`.
- `Services/Agent365ObservabilityService.cs` — Implemented real `StartInvokeAgentScope` and `StartInferenceScope` methods using SDK types (`InvokeAgentScope.Start`, `InferenceScope.Start`). All scope creation wrapped in try/catch with fail-safe design (returns null on error, logs warning).

**SDK types used:**
- `InvokeAgentScope.Start(InvokeAgentDetails, TenantDetails, Request?, string? conversationId)` — creates agent invocation scope for request entry points
- `InferenceScope.Start(InferenceCallDetails, AgentDetails, TenantDetails)` — creates inference scope for LLM calls
- `AgentDetails(agentId, agentName)` — lightweight identity using ClientAppId
- `InvokeAgentDetails(AgentDetails, Uri endpoint, string sessionId)` — agent invocation metadata
- `TenantDetails(Guid)` — tenant ID wrapper
- `InferenceCallDetails(operationName, model, providerName, inputTokens, outputTokens)` — LLM call metadata
- `Request(content)` — optional prompt content wrapper

**Key design decisions:**
- **Fail-safe observability:** All scope creation returns null on any exception. Observability failures must NEVER break request flow.
- **SDK version 0.1.75-beta specifics:** InferenceScope.Start takes 3 positional arguments (InferenceCallDetails, AgentDetails, TenantDetails), not the documented newer API. Adapted to match actual package API surface.
- **OpenTelemetry integration:** Configured OTel with A365 source tracing (`Microsoft.Agents.A365.*`). SDK's internal exporter is automatically picked up.
- **Placeholder endpoint:** Using `https://apim.example.com` as endpoint in InvokeAgentDetails (SDK requires URI, but our APIM scenario doesn't have a fixed agent endpoint).

**Patterns followed:**
- Alias `A365Request` to resolve namespace conflict with `Azure.Core.Request`
- All constructors use named parameters for clarity
- Tenant ID parsed to Guid (service validates it's valid earlier in pipeline)
- Correlation ID used for both sessionId and conversationId
- No-op service remains unchanged (returns null for all methods when disabled)

**Test results:** 231/231 tests pass (4 skipped), 0 regressions. Build clean.

**Decision:** Observability is production-ready. No more TODOs or stub comments. The service will emit real Agent365 telemetry when enabled.
