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
- 2026-05-21: The `/apis` page render loop came from `loadInitialData` depending on `operationsByApi` while also resetting that state to a fresh `{}`, which changed the callback identity and re-fired the mount effect forever.
- Rule: if an effect triggers a callback that mutates local maps/arrays, keep the callback keyed to stable inputs and read the latest collection through a ref or stable ID instead of adding the collection itself to the callback deps.

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

## 2026-05-22 — Flex+Truncate Pattern for Badge/Title Rows

**Layout Bug Fix Session:**
- Fixed text overflow in API tree rows and modal parameter cards
- Pattern: when a title and badge(s) share a flex row, the title needs `min-w-0 flex-1 truncate` and badges need `flex-shrink-0`
- Without `min-w-0`, flex items won't shrink below intrinsic content width, causing overflow
- Removed redundant `serviceUrl` from API tree (path is sufficient, URL cluttered the row)
- Simplified operation rows: show method badge + urlTemplate instead of duplicated `displayName` + verb + badge
- Modal horizontal scroll fixed with `overflow-x-hidden` on dialog container
- Parameter card grid changed from `md:grid-cols-2` to `sm:grid-cols-2` for narrower modal viewport fit

**Rule:** For any flex row with text + badges: `<span class="min-w-0 flex-1 truncate">Text</span><Badge class="flex-shrink-0">Label</Badge>`

## 2026-05-21 — AAA M6 UI Pending (Access Profile Admin Page)

**Status:** Pending — awaiting M3 precheck contract finalization

**Scope:** Build `/access` page (new admin UI for Access Profiles)

**Layout & Components:**
- **Top:** Client selector (dropdown/search from existing `GET /api/clients`)
- **Main grid:** APIs (rows) with columns: Plan, Routing Policy, Deployments allowed, Enable toggle
- **Drill-down:** Click API row to expand operations with per-operation overrides
- **Add/Edit form:** Select Plan (dropdown from existing plans), optionally select Routing Policy, optionally restrict deployments
- **Bulk action:** "Apply to multiple APIs" — select APIs from checklist, assign same profile to all in one shot

**Reuse:** Plan selector dropdown (already built for client assignment), Routing Policy selector (already built for Plans page)

**Client First Workflow:** Primary user journey is "configure THIS client's access to various APIs" — not "which clients use this API". So the layout starts with client selector, then shows their API matrix.

**Integration:** POST/PUT/DELETE via `/api/access-profiles/*` (Freamon M2). Trigger profile creation when form submits.

**Validation:** Contract awaits M3 precheck integration (apiId/operationId handling) and M4 log-ingest (audit trail).

**Next:** Start after M2 API contracts firm (2-3 days out).

### 2026-05-21T21:48:19Z — AAA M5 UI (`/access` Page) In-Flight

**Status:** 🔄 IN-FLIGHT

**Scope:**
Build the `/access` admin page for Access Profile management (new per-client, per-API authorization overrides).

**Layout & Components:**
- **Top Section:** Client selector (dropdown/search from `/api/clients`)
- **Main Grid:** API rows with columns:
  - Plan (read-only, shows from profile or inherited)
  - Routing Policy (select or null for inherit)
  - Deployments Allowed (multi-select or null for inherit)
  - Enable/Disable toggle
- **Drill-down:** Click API row to reveal operations table with per-operation overrides (same column structure)
- **Add/Edit Form:** Modal with Plan selector, optional Routing Policy, optional deployment restrictions, submit/cancel
- **Bulk Action:** Select multiple APIs from checklist, apply the same profile to all in one shot

**Reuse Existing Components:**
- Plan selector dropdown (`.squad/skills/` available)
- Routing Policy selector (already built for Plans page)
- Tailwind flex+truncate pattern for row overflow (`.squad/skills/tailwind-flex-truncate-pattern/SKILL.md`)
- React render-loop debugging skill (`.squad/skills/react-render-loop-debugging/SKILL.md` — avoid Apis.tsx pattern)

**API Contract:**
- GET `/api/access-profiles` — list profiles for a client
- POST/PUT `/api/access-profiles/{id}` — create/update profile
- DELETE `/api/access-profiles/{id}` — delete profile
- Bulk endpoint (TBD via Freamon M2 spec)

**Blockers Now Cleared:**
- ✅ Freamon M1-M3: Precheck contract finalized (apiId/operationId path ready)
- ✅ Bunk: 21-test matrix validates response shapes

**Parallel to M4:**
- Sydnor's M4 template updates do not block UI work
- API endpoint contracts already firm

**Next Steps:**
- Start component structure (ClientSelector, ApiGrid, OperationGrid, ProfileForm)
- Implement data fetch + caching patterns (parallel to API updates)
- Polish flex+truncate styling for API/operation rows
- Mock data for component testing before API integration

## 2026-05-21T22:07:10Z — AAA M5 Admin UI Complete

**Status:** ✅ COMPLETE

**Commits:**
- Kima M5: `ec54c29c`

**Delivered:**
- New `/access` admin page for Access Profile management
  - **Client selector** (top): Searchable client dropdown using existing `/api/clients`
  - **API grid** (main): Rows for each API, columns for Plan (read-only), Routing Policy (select), Deployments Allowed (multi-select), Enable toggle
  - **Drill-down:** Click API row to expand operations table with per-operation override fields
  - **Add/Edit form:** Modal with Plan selector, optional Routing Policy, optional deployment restrictions
  - **Bulk action:** Select multiple APIs, apply same profile to all via `/api/access-profiles/bulk`
- Extracted shared `useApimCatalog` hook: lifted from `/apis` page, now used by both `/access` and `/apis` for APIM catalog loading
  - Render-loop-safe ref/callback pattern preserved
  - Eliminates duplicate API/operation loading logic
  - Backward-compatible with existing `/apis` page
- UI behavior:
  - Empty cells show effective cascade result (not blank)
  - Direct overrides visually distinct from inherited values (CascadeBadge component)
  - Disabled profiles stay visible, treated as non-winning per backend cascade
  - API cards lazy-load operations when expanded
  - Editor supports both single-scope and bulk creation

**Validation:**
- ✅ `cd src\aipolicyengine-ui && npm run lint` — passed
- ✅ `cd src\aipolicyengine-ui && npm run build` — passed
- ✅ Shared hook refactored without breaking `/apis` page

**Files Created/Modified:**
- `src/aipolicyengine-ui/src/App.tsx` — Route wiring for `/access`
- `src/aipolicyengine-ui/src/components/Layout.tsx` — Navigation item
- `src/aipolicyengine-ui/src/components/ui/dialog.tsx` — Drawer-style support via `contentClassName`
- `src/aipolicyengine-ui/src/hooks/useApimCatalog.ts` — Shared catalog loading hook
- `src/aipolicyengine-ui/src/api/accessProfiles.ts` — API client (NEW)
- `src/aipolicyengine-ui/src/types/accessProfiles.ts` — Type definitions (NEW)
- `src/aipolicyengine-ui/src/pages/AccessProfiles.tsx` — Main page (NEW)
- `src/aipolicyengine-ui/src/pages/Apis.tsx` — Refactored to use shared hook
- `src/aipolicyengine-ui/src/components/accessProfiles/CascadeBadge.tsx` — Cascade indicator (NEW)
- `src/aipolicyengine-ui/src/components/accessProfiles/ClientList.tsx` — Client selector (NEW)
- `src/aipolicyengine-ui/src/components/accessProfiles/ProfileEditor.tsx` — Edit form (NEW)
- `src/aipolicyengine-ui/src/components/accessProfiles/ProfileGrid.tsx` — Matrix view (NEW)
- `src/aipolicyengine-ui/src/components/accessProfiles/types.ts` — UI types (NEW)

**Learning:** Shared hooks extracted from page-specific implementations reduce code duplication and improve maintainability. The render-loop debugging pattern (ref + stable callback identity) is now reusable across all APIM integration pages.

**Cross-Team Notes:**
- Freamon M1-M3 backend contracts validated; all CRUD + bulk endpoints consumed
- Sydnor M4 templates shipped with metadata propagation; `/access` page now shows all cascade levels
- Bunk all 21 AAA tests active and passing; UI integration complete