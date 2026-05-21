# Project Context

- **Owner:** Zack Way
- **Project:** AI Policy Engine — APIM Policy Engine management UI for AI workloads, implementing AAA (Authentication, Authorization, Accounting) for API management. Built for teams who need bill-back reporting, runover tracking, token utilization, and audit capabilities. Telecom/RADIUS heritage.
- **Stack:** .NET 9 API (Chargeback.Api) with Aspire orchestration (Chargeback.AppHost), React frontend (chargeback-ui), Azure Managed Redis (caching), CosmosDB (long-term trace/audit storage), Azure API Management (policy enforcement), Bicep (infrastructure)
- **Created:** 2026-03-31

## Key Files

- `src/chargeback-ui/` — React frontend (my primary workspace)
- `src/chargeback-ui/package.json` — Frontend dependencies

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

## 2026-05-21 — APIs management UI (M4)

- Added APIM UI under `src/aipolicyengine-ui/src/pages/Apis.tsx` with dedicated client/types files in `src/api/apim.ts` and `src/types/apim.ts`; keep APIM shapes separate from legacy dashboard DTOs.
- For list/detail admin pages, the current pattern is Tailwind + local state: left tree/list in a `Card`, right details/actions in a second `Card`, dialogs for destructive/assignment flows, and inline fixed-position toast messaging for retryable network failures.
- APIM status polling is UI-driven: after a 202 apply response, set optimistic `applying` state and poll `GET .../policy` every 2 seconds until status leaves `pending`/`applying`.
- Template parameter defaults should prefer the current assignment, then template defaults, and only shared plan-level values; there is no contract yet to map a specific plan to an API assignment, so avoid guessing per-plan defaults.
- The SPA now maps top-level tabs to pathname routes in `App.tsx` (including `/apis`) without adding a router dependency; keep using this lightweight history API pattern unless the app adopts React Router later.

## 2026-05-21 — ASP.NET Core Nested Configuration Convention (FYI)

**Informational Context for Future Backend Config**

Freamon fixed a config-binding bug in the APIM infrastructure: the env var `APIM_RESOURCE_ID` does not bind to nested config keys in ASP.NET Core. The standard convention is **double underscore**: `Apim__ResourceId`.

**Pattern for Reference:**
- C# class `ApimManagementOptions` bound to section `"Apim"`
- Config key in code: `Apim:ResourceId` (colon)
- Environment variable: `Apim__ResourceId` (double underscore)

**If Frontend Consumes Similar Config Later:**
- Backend will emit env vars using this convention (e.g., `Foo__Bar__Baz` for nested settings)
- When frontend reads backend config, expect the same pattern
- This is idiomatic ASP.NET Core, not a special case

**Full decision merged into `.squad/decisions.md`.**