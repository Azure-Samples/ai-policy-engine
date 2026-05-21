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

## Core Context

**Architecture Reviews & Decisions (2026-03-31 to 2026-04-11):**

Phase 0–2 Code Review (2026-04-01): CONDITIONALLY APPROVED. 3 blocking issues fixed:
1. Request quota not enforced for multiplier billing plans → fixed in precheck
2. Frontend billingPeriod type mismatch (object vs. string) → fixed
3. Frontend RouteRule missing priority/enabled fields → added to UI

8 should-fix items addressed: deleted dead repository code, fixed race condition in container initialization, added RoutingPolicyId to audit trail, fixed TypeScript type safety, secured outbound log JSON, made pricing cache thread-safe, improved error handling.

Strong fundamentals: Repository pattern solid, RoutingEvaluator pure/stateless, multiplier math correct, authorization consistent, 200+ tests passing, APIM integration works, backward-compatible.

Phase 2 Real Implementation (2026-04-11): Agent365 Observability SDK deployed. Real scope calls (InvokeAgentScope, InferenceScope) with fail-safe design. Manual OTelemetry config (AddA365Tracing unavailable in v0.1.75-beta). Namespace conflict resolved (A365Request alias). 235 tests passing, zero regressions.

Phase 3 Complete (2026-04-11): APIM auto-router policies deployed. Both auth types (subscription-key, entra-jwt) support routing. Policies extract routedDeployment from precheck, rewrite backend URL, extend logging with routing metadata.

**Deployment Status:**

All backend features (routing, pricing, observability) complete and tested. Infrastructure ready via azd + Terraform (77 resources provisioned in 9m59s on 2026-05-14). Application running on Container App. APIM policies configured. All systems operational.

## Learnings

**2026-05-16 — Non-AI API Limits Architecture:**
- Chose flat fields (`NonAiRequestsPerMinute`, `NonAiMonthlyRequestQuota`) over sub-object — consistency with existing schema pattern.
- Chose dedicated `/api/precheck-rest` endpoint over extending existing precheck — separation of concerns, avoids polluting AI hot path.
- Chose Redis counters (same pattern as AI RPM) over APIM built-in `rate-limit-by-key` — dashboard visibility is non-negotiable for this engine.
- Monthly counter lives on `ClientPlanAssignment.NonAiCurrentPeriodRequests`, same Cosmos+Redis pattern as token usage.
- No schema migration needed — CosmosDB is schema-less, defaults to 0 (unlimited = no enforcement for existing plans).
- Spec delivered to `.squad/decisions/inbox/mcnulty-non-ai-api-limits-architecture.md`.

**2026-05-16 — APIM Policy Management Architecture:**
- Chose **Tier B (template apply)** — users pick templates + fill params, engine renders XML and pushes to APIM. No raw XML editor (too risky for v1). Drift detection deferred to M6.
- Chose **`Azure.ResourceManager.ApiManagement` SDK** over ARM REST or Terraform — idiomatic .NET, strongly typed, DefaultAzureCredential, no preview risk.
- **Reshapes non-AI architecture:** Sydnor's `entra-jwt-rest-policy.xml` ships as-is, then immediately becomes the seed for the `entra-jwt-rest` template. Precheck-rest endpoint stays as an alternative enforcement mode but APIM-native `rate-limit-by-key` is the default in the template.
- Plans page sets plan-level default limits; new APIM Management page assigns templates per-API with those defaults pre-populated as parameter values.
- Custom RBAC role (narrow: apis/read + policies/read+write) instead of broad `API Management Service Contributor`.
- Storage: existing `configuration` container, new `policy-assignment` partition key document type.
- Spec delivered to `.squad/decisions/inbox/mcnulty-apim-management-architecture.md`.

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