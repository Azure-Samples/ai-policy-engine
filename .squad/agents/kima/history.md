# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `src/chargeback-ui/` — React frontend (my primary workspace)
- `src/chargeback-ui/package.json` — Frontend dependencies

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-31 — Phase 0 Complete: Backend Storage Architecture Established

**Phase 0 Status:** ✅ COMPLETE (Freamon + Bunk)

The backend storage architecture has been refactored from Redis-only to a durable CosmosDB source-of-truth pattern with Redis as a write-through cache. This is the foundational layer for all upcoming work (routing, pricing, policy enhancements).

**Key Implications for Frontend:**
- **Backend API contracts unchanged** — All endpoint signatures remain the same. The refactoring is internal (storage layer only).
- **Data durability improved** — Configuration data (plans, clients, pricing, routing policies) now survives Redis restarts and evictions.
- **Performance unchanged** — Redis remains the read cache; startup is now slightly slower due to cache warming, but request latency is identical.
- **New Repositories Pattern** — Future frontend changes will interact with the same API endpoints, which now use `IRepository<T>` abstraction instead of direct Redis.

**What Kima Needs to Know:**
- Phase 1 (Model Routing) will add new fields to the precheck response: `routedDeployment` (the actual deployment after routing is applied).
- Future billing UI will need to adapt based on plan configuration (Phase 2–3 multiplier pricing work).
- No frontend code changes required for Phase 0 — backend refactoring only.
- Phase 0 completes the architectural debt fix; Phase 1 onwards adds new features without storage concerns.

**Test Results:** 129/129 tests pass (36 new Phase 5 tests for repositories/migration/warmup).

### 2026-03-31 — Phase 1 Complete: Backend API Stable for Phase 4 Frontend Work

**Phase 1 Status:** ✅ COMPLETE (Freamon + Bunk)

Backend storage architecture, model routing, and multiplier pricing are complete. All API contracts finalized. Ready for frontend adaptive UI implementation.

**What Kima Needs to Know for Phase 4:**

- **Backend is Stable:** No breaking changes planned. All routing + pricing features are finalized and tested (200/200 tests pass).
- **New Precheck Response Fields:** The precheck endpoint now returns `routedDeployment` (actual deployment after routing), `requestingDeployment`, and `routingPolicyId`. Frontend can use these for diagnostic dashboards.
- **Request Summary Export Ready:** New endpoints available:
  - `GET /api/chargeback/request-summary?clientId=...&startDate=...&endDate=...` — query request usage by period
  - `GET /api/export/request-billing?format=csv` — download request billing data
- **Multiplier Billing UI:** Plans now have `UseMultiplierBilling` flag. Dashboard should adapt:
  - If ALL plans use multiplier → show only request-based views (no token UI)
  - If ALL plans use token → show only token-based views (no multiplier UI)
  - If MIXED → show hybrid view (both models visible)
  - Applies to dashboards, usage views, client detail pages, export options
- **Tier Tracking:** Clients now track `RequestsByTier` (e.g., Standard, Premium, Enterprise). Dashboard can show per-tier breakdowns and cost analysis.
- **Request Utilization:** ClientUsageResponse includes `CurrentPeriodRequests`, `OverbilledRequests`, and `RequestUtilizationPercent`. Dashboard can show quota usage and overage alerts.
- **Backward Compat:** All new fields are nullable. Existing dashboard code continues to work. New UI is additive.

**Ready for Phase 4 Frontend Development:**
- Plan response includes `UseMultiplierBilling`, `MonthlyRequestQuota`, `OverageRatePerRequest`
- Client detail response includes `CurrentPeriodRequests`, `OverbilledRequests`, `RequestsByTier`, `RequestUtilizationPercent`
- Model pricing includes `Multiplier`, `TierName`
- Export endpoints ready for download functionality
- No API breaking changes — pure feature additions

**Test Results:** 200/200 tests pass (30 new Phase 2 integration tests from Bunk B5.7 + B5.8).

