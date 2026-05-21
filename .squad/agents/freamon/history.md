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

## Core Context

**Completed Backend Phases (2026-03-31 to 2026-04-11):**

Phase 0 (Storage): Migrated from Redis-only to CosmosDB source-of-truth. Implemented repository pattern (IRepository<T>). All 4 config entities now durable: plans, clients, pricing, usage policies. Redis is write-through cache. Startup services: RedisToCosmossMigrationService, CacheWarmingService. 129 tests passing.

Phase 1 (Model Routing Foundation): Added ModelRoutingPolicy entity with CRUD endpoints. Extended models with multiplier billing (Multiplier, TierName) and request-based quotas (MonthlyRequestQuota, CurrentPeriodRequests). Created CosmosRoutingPolicyRepository. All 10 work items (F1.1–F1.10) complete. New fields have safe defaults (existing data backward-compatible).

Phase 2 (Model Routing Enforcement): Implemented 7 routing enforcement endpoints (F2.1–F2.7). Precheck evaluates routing policies, returns routedDeployment. Multiplier pricing applied per-request. Rate limiting scoped to routed deployment. Audit trail includes pricing metadata (Multiplier, EffectiveRequestCost, TierName). All API contracts stable. 200 tests passing.

Phase 2b (Agent365 Observability): Integrated Microsoft.Agents.A365.Observability SDK v0.1.75-beta. Implemented real scope calls (InvokeAgentScope for Precheck, InferenceScope for LogIngest). Manual OpenTelemetry config (AddA365Tracing not available in beta SDK). Fail-safe design (null returns on exceptions). 235 tests passing, zero regressions.

**All Phases Ready for Production:**

API fully functional with:
- Durable configuration storage (Cosmos + Redis cache)
- Model routing with flexible policy rules
- Per-request multiplier billing + tier support
- Enterprise observability (Agent365 scopes)
- Rate limiting by routed deployment
- APIM policy integration ready
- Comprehensive test coverage (235 tests)

Backend is feature-complete and awaiting infrastructure deployment.

## Learnings

*Core learnings consolidated in Core Context section above (see git history for detailed entries).*

## Archived Learnings (Pre-May 2026)

All development work from Phase 0–3 (2026-03-31 to 2026-05-14) is documented in Core Context and git commit history. Key achievements:
- Phase 0: Cosmos + Redis storage architecture
- Phase 1: Model routing policies + multiplier billing
- Phase 2: Agent365 Observability integration
- Phase 3: APIM policy variants and infrastructure
- Infrastructure: Terraform + azd deployment (77 resources)

For detailed work items, see:
- .squad/decisions.md — architectural decisions
- .squad/orchestration-log/ — agent completion logs
- git log --oneline — implementation history

## APIM Policy Management Learnings (2026-05-21)

- `Azure.ResourceManager.ApiManagement` 1.3.x works cleanly with `ArmClient` + `DefaultAzureCredential`, but the APIM resource handle should be created lazily from `Apim:ResourceId` so unrelated app startup/tests do not fail when APIM is unconfigured. Read policy XML with `PolicyExportFormat.RawXml` and write with `PolicyContentFormat.RawXml` to preserve round-trippable XML instead of fragment-expanded output.
- Template loading is safest as a repo-shipped library under `policies/templates/{id}/` with `policy.xml` + `template.json`. Validate manifests against placeholders discovered by regex before serving them, then render with exact `{{Placeholder}}` replacement, normalize typed parameters (`string`, `int`, etc.), reject unknown/unfilled inputs, and parse the rendered XML to confirm a `<policies>` root before any apply call.
- Async apply is better implemented as an in-process channel + `BackgroundService` than ad-hoc `Task.Run` from endpoints. Endpoints persist the desired assignment as `pending`, enqueue a scope work item, and return 202 immediately; the worker flips to `applying`, re-renders from stored parameters, applies through the SDK, computes `generatedXmlHash` on success, and records `failed/errorMessage` on exceptions. Startup replay of `pending`/`applying` items should be best-effort so tests or partial environments do not stop the host.
- For Bunk: the APIM seams are now interface-first (`IApimCatalogService`, `ITemplateLibraryService`, `IPolicyAssignmentRepository`, `IApimPolicyApplyService`) and the worker logic is isolated in `ApimPolicyApplyService.ProcessAssignmentAsync`. Unit tests can exercise template rendering and apply orchestration without live Azure; recorded/live APIM coverage should focus on `ApimCatalogService` method mappings and the raw-XML policy format behavior.