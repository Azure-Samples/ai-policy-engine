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

### 2026-03-31 — Phase 4 Complete: Frontend UI for Routing & Multiplier Billing

**Phase 4 Status:** ✅ COMPLETE (Kima K4.1–K4.9)

All frontend work for model routing policies and multiplier billing is implemented. Build passes, lint clean (no new issues).

**What Was Built:**

- **K4.8 — TypeScript types:** `ModelRoutingPolicy`, `RouteRule`, `RoutingBehavior`, `PlanDataExtended`, `ClientAssignmentExtended`, `ModelPricingExtended`, `RequestSummaryResponse`, `BillingMode` in `types.ts`
- **K4.9 — API client:** CRUD for routing policies, request summary fetch, request billing export download in `api.ts`
- **K4.1 — Routing Policies page:** Full CRUD with rule builder (deployment picker or manual input), default behavior selector, fallback deployment, "used by plans" indicator, delete warning
- **K4.2 — Plans page extended:** Routing policy selector, UseMultiplierBilling toggle, MonthlyRequestQuota/OverageRatePerRequest fields (conditionally visible), Billing Mode and Routing Policy columns in table
- **K4.3 — Clients page extended:** Routing policy override selector, effective request usage display with progress bar + tier breakdown badges, routing override column in table
- **K4.4 — Pricing page extended:** Multiplier column (color-coded: green < 1.0, amber > 1.0), TierName column with badges, multiplier/tier fields in create/edit dialog
- **K4.5 — Request Billing dashboard:** KPI cards (total/effective/overbilled/active clients), bar chart by client, donut chart by tier, overage alerts with progress bars, per-client summary table. Adaptive: only visible when multiplier billing plans exist
- **K4.6 — Client detail extended:** Request billing section with quota gauge, overbilled requests card, tier pie chart, requests-by-model table. Only shown when client's plan uses multiplier billing
- **K4.7 — Export page extended:** Request Billing Export card with period selector. Adaptive: only visible when multiplier billing plans exist

**Adaptive UI Logic (per Zack's directive):**
- `BillingMode` type: `'token' | 'multiplier' | 'hybrid'`
- App.tsx computes billing mode from plan data, passes to Layout
- Layout conditionally shows "Request Billing" nav tab
- RequestBilling page shows empty state when no multiplier plans
- Export shows request billing card only when multiplier plans exist
- ClientDetail shows request billing section only for multiplier-billed plans

**Architecture Decisions:**
- Extended existing types with `PlanDataExtended`, `ClientAssignmentExtended`, `ModelPricingExtended` to avoid breaking existing code
- No new dependencies — reused Recharts, Lucide, Tailwind, existing component library
- Followed existing patterns: `useCallback` data loading, `authFetch` wrapper, Card/Table/Badge/Dialog components
- Routing Policies is always visible (routing is useful regardless of billing mode)

**Build Results:** `tsc -b && vite build` succeeds. Lint: 9 pre-existing errors, 0 new.

### 2026-03-31 — Session Complete: All 5 Phases Delivered

**Project Status:** ✅ COMPLETE

All work is done. Phase 0 (storage), Phase 1 (routing + pricing), Phase 2 (enforcement), Phase 3 (APIM policies), Phase 4 (frontend UI), Phase 5 (testing) all complete. 222 tests passing. Full end-to-end system operational.

**Kima's Contributions:**
- Phase 4 (K4.1–K4.9): Frontend UI for model routing policies and multiplier billing, adaptive billing dashboards, routing policy CRUD page, request billing exports

**What's Ready for Deployment:**
- React frontend with all routing and billing UI components
- Adaptive UI logic: billing mode computed from plan configuration (token/multiplier/hybrid)
- Full CRUD for routing policies, detailed client billing views, tier-based analytics
- Integration with all backend API endpoints
- TypeScript strict mode compliant, no new linting issues

**User Experience:**
- Dashboard auto-adapts based on billing configuration
- Routing policies fully manageable from UI
- Request billing tracking with per-client, per-tier analytics
- Export functionality for billing data

**Next Phase (Future):**
- Advanced policy engine UI for enforced model rewrites
- Custom dashboard builder
- Audit log UI for policy change history

### 2026-03-31 — Code Review Fix Pass: 5 Findings Resolved

**Context:** McNulty reviewed the frontend and flagged 5 issues (2 bugs, 3 suggestions). All fixes implemented, tsc + vite build clean.

**What Changed:**

1. **B2 — billingPeriod type mismatch:** `RequestSummaryResponse.billingPeriod` changed from `{ year: number; month: number }` to `string` (YYYY-MM format matching backend). `RequestBilling.tsx` now displays the string directly instead of accessing `.month`/`.year`.

2. **B3 — RouteRule missing fields:** Added `priority: number` and `enabled: boolean` to `RouteRule`. `RoutingPolicies.tsx` rule builder now includes a priority number input (auto-incremented on add) and an enabled checkbox (defaults to true). Existing rule display shows priority badge and enable/disable toggle.

3. **S5 — ModelPricing base type consolidation:** Added `multiplier` and `tierName` to base `ModelPricing` and `ModelPricingCreateRequest`. Eliminated all `Extended` type variants (`ModelPricingExtended`, `PlanDataExtended`, `ClientAssignmentExtended`) by folding their fields into the base types (`PlanData`, `ClientAssignment`). Removed all `as Extended` casts across 7 component files.

4. **S6 — Plan request type safety:** Added `modelRoutingPolicyId`, `monthlyRequestQuota`, `overageRatePerRequest`, `useMultiplierBilling` to `PlanCreateRequest` and `PlanUpdateRequest`. Plans.tsx no longer bypasses type checking via object spread.

5. **S8 — Rich API error messages:** Added `parseErrorMessage()` helper to `api.ts` that extracts `error` or `message` from backend JSON responses. Applied to all 24 API functions. Users now see actionable messages (e.g., "Deployment not allowed") instead of generic "Bad Request".

**Learnings:**
- Extended types as band-aids accumulate tech debt quickly — fold fields into base types early
- API error parsing should be a shared helper, not copy-pasted per function
- Backend returns structured JSON errors — always parse them for the UI

**Build Results:** `tsc -b` clean, `vite build` succeeds (2556 modules, 11.5s).

