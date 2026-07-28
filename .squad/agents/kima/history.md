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

## 2026-06-26 — Access Profiles Layout Review & Accessibility Fixes

**Status:** ✅ COMPLETE

**Commits:**
- Original coordinator work: `862fc5d5`
- Kima accessibility & offset fixes: `4212be7`

**Scope:**
Reviewed and fixed layout/accessibility issues on the `/access` Access Profiles page after coordinator implemented sticky columns and search/filter controls.

**Changes Reviewed:**
1. **Sticky left column** (xl breakpoint) — ClientList pinned with independent scroll
2. **Search bar** — Full-text search across API, operation, plan, method, path
3. **Override filter** — Three modes (All scopes / Direct overrides / Inherited only) with live count and clear control

**Issues Found & Fixed:**
- **Sticky offset mismatch**: Header is `h-16` (4rem) but original used `top-[5.5rem]` (88px), creating a 24px gap. Fixed to `top-16` to align with header bottom edge.
- **Client list height calc error**: Original used `h-[calc(100vh-7rem)]` but should be `h-[calc(100vh-4rem)]` to match the `top-16` offset.
- **Missing accessibility labels**: 
  - Search inputs lacked `aria-label` for screen readers — added `aria-label="Search clients"` and `aria-label="Search access profiles"`.
  - Override filter buttons needed semantic markup — added `role="group"`, `aria-label="Override filter"`, and `aria-pressed` to each button.

**Responsive Behavior Verified:**
- Below xl: client list scrolls normally with `max-h-[60vh]`, no sticky behavior.
- At xl+: client list sticks to viewport with `xl:sticky xl:top-16`, fills remaining height with `xl:h-[calc(100vh-4rem)]`, scrolls internally.
- Filter controls stack vertically on mobile (flex-col), row layout on lg+ (flex-row).

**Component Patterns Followed:**
- Flex+truncate pattern for badges and titles (existing skill)
- Sticky positioning consistent with existing page headers
- Search icon absolute positioning with left padding on input
- Filter buttons use Tailwind `cn()` utility for conditional classes

**Validation:**
- ✅ `npx tsc -b` — clean
- ✅ `npx eslint` — clean
- ✅ `npm run build` — success

**Files Modified:**
- `src/aipolicyengine-ui/src/pages/AccessProfiles.tsx` — Sticky offset and height calc
- `src/aipolicyengine-ui/src/components/accessProfiles/ClientList.tsx` — Search aria-label
- `src/aipolicyengine-ui/src/components/accessProfiles/ProfileGrid.tsx` — Sticky offset, search aria-label, filter group semantics

**Key Learnings:**
- **Sticky offset rule**: Always match `top-*` to header height exactly (header is `h-16` → sticky uses `top-16`, not arbitrary pixel values).
- **Calc height pattern**: When pinning to viewport with `top-N`, use `h-[calc(100vh-N)]` with the same offset value for seamless fill.
- **Filter button a11y pattern**: Button groups need `role="group"` + `aria-label` on container, `aria-pressed` on each toggle button for proper screen reader announcement.
- **Search input a11y**: Icon-only inputs must have `aria-label` even when placeholder exists — placeholders are not accessible labels.

**Cross-Team Notes:**
- Coordinator implemented feature correctly; only needed offset/a11y polish.
- No breaking changes; filter logic and count math are correct (visibleScopeCount handles expanded sections properly).
- Layout is production-ready after fixes.

## 2026-06-26 — Access Profiles Filter Refactor (McNulty Review)

**Status:** ✅ COMPLETE

**Commits:**
- Architecture refactor: `603d5ce4`

**Scope:**
McNulty's code review requested refactor of filter state/logic from ProfileGrid component up to AccessProfiles page to follow established container/presentation pattern.

**Blocking Issue:**
ProfileGrid owned ~170 lines of filter state, business logic, and data transformation (searchQuery/overrideFilter state, cellMatchesSearch/cellMatchesOverride helpers, filteredSections/visibleScopeCount memos). This violated the established pattern where AccessProfiles.tsx owns data transformation and ProfileGrid owns rendering.

**Refactor Completed:**
- **Lifted state**: Moved `searchQuery` and `overrideFilter` from ProfileGrid to AccessProfiles
- **Lifted logic**: Moved `cellMatchesSearch` and `cellMatchesOverride` helpers to page level (useCallback for stability)
- **Lifted filtering**: Moved `filteredSections` and `visibleScopeCount` useMemo computations to page
- **Updated ProfileGrid props**: Now receives pre-filtered data (`filteredSections`, `globalVisible`, `visibleScopeCount`) and filter UI state/callbacks (`searchQuery`, `overrideFilter`, `onSearchChange`, `onOverrideFilterChange`, `onClearFilters`)
- **Preserved behavior**: All search, override filter modes, match counts, empty states, and collapsed-section handling work identically

**Pattern Now Consistent:**
- AccessProfiles.tsx: Owns client selection, API catalog, section construction, expansion state, **filter state**, and assignment workflows
- ProfileGrid.tsx: Renders pre-computed sections with cascade badges, search/filter UI controls, but no filtering logic
- Matches existing expand/collapse pattern where page manages `expandedApiIds` and passes `section.expanded` flag to grid

**Benefits:**
- Clean separation: data transformation (page) vs presentation (component)
- Future filter features (saved presets, URL filters, bulk operations on filtered scopes) integrate cleanly without refactoring
- Filter logic testable independently of UI (pure functions + memos at page level)
- No state sync bugs between page and grid
- Consistent with Plans, APIs, Routing pages

**Validation:**
- ✅ `npx tsc -b` — clean
- ✅ `npx eslint` — clean
- ✅ `npm run build` — success

**Files Modified:**
- `src/aipolicyengine-ui/src/pages/AccessProfiles.tsx` — Added filter state, helpers, memos; passes pre-filtered data to ProfileGrid
- `src/aipolicyengine-ui/src/components/accessProfiles/ProfileGrid.tsx` — Removed filter state/logic; receives pre-filtered data as props

**Key Learning:**
- **Container/presentation pattern**: Page components own state, logic, and data transformation; child components receive computed props and callbacks, focus on rendering.
- **Refactor signal**: When a component has useState + useMemo for derived data that depends on parent-owned collections (sections, apis), the state likely belongs in the parent.
- **Prop explosion is OK**: Passing 20+ props is acceptable when it maintains clear responsibility boundaries; prefer explicit props over implicit state management.

## 2026-06-26 — Filter Logic Extraction for Testing (Bunk Coordination)

**Status:** ✅ COMPLETE

**Commits:**
- Pure function extraction: `4fc0a5a6`

**Scope:**
Final cleanup after McNulty review — extracted filter logic into pure, testable module to prepare for Bunk's unit tests.

**Refactor:**
- Created `src/aipolicyengine-ui/src/components/accessProfiles/filtering.ts` — dedicated module for all filter logic
- **Exported pure functions:**
  - `cellMatchesSearch(cell, normalizedQuery, plansById): boolean` — search matching logic
  - `cellMatchesOverride(cell, filter): boolean` — override filter logic
  - `selectFilteredView(sections, globalCell, searchQuery, overrideFilter, plansById): FilteredView` — complete filtering with sections, visibility, count
- **Exported types/constants:**
  - `OverrideFilter` type alias
  - `OVERRIDE_FILTERS` constant array
  - `AccessApiSection`, `FilteredSection`, `FilteredView` interfaces
- **AccessProfiles.tsx changes:**
  - Imports `selectFilteredView` and `OverrideFilter` from filtering module
  - Calls `selectFilteredView` in useMemo with state inputs
  - Receives `{ filteredSections, globalVisible, visibleScopeCount, filtersActive }` as result
- **ProfileGrid.tsx changes:**
  - Imports `OverrideFilter`, `OVERRIDE_FILTERS`, and `FilteredSection` from filtering module
  - Removed local type/constant definitions (now imported)

**Benefits:**
- **Unit testable**: All filter logic is pure functions (no side effects, no component mounting required)
- **Separation complete**: State in page (AccessProfiles), logic in module (filtering.ts), rendering in component (ProfileGrid)
- **Ready for Bunk**: Pure functions export makes test writing trivial
- **McNulty satisfied**: Logic completely out of rendering components

**Pattern:**
```typescript
// filtering.ts (pure functions)
export function selectFilteredView(sections, globalCell, query, filter, plans): FilteredView { ... }

// AccessProfiles.tsx (state + calls pure helper)
const [searchQuery, setSearchQuery] = useState("")
const [overrideFilter, setOverrideFilter] = useState<OverrideFilter>("all")
const { filteredSections, globalVisible, visibleScopeCount, filtersActive } = useMemo(
  () => selectFilteredView(sections, globalCell, searchQuery, overrideFilter, plansById),
  [sections, globalCell, searchQuery, overrideFilter, plansById],
)

// ProfileGrid.tsx (rendering only)
<ProfileGrid filteredSections={filteredSections} ... />
```

**Validation:**
- ✅ `npx tsc -b` — clean
- ✅ `npx eslint` — clean
- ✅ `npm run build` — success

**Files:**
- `src/aipolicyengine-ui/src/components/accessProfiles/filtering.ts` — NEW pure logic module
- `src/aipolicyengine-ui/src/pages/AccessProfiles.tsx` — Imports and calls filtering module
- `src/aipolicyengine-ui/src/components/accessProfiles/ProfileGrid.tsx` — Imports types/constants from filtering module

**Next:** Bunk will add unit tests for filtering.ts functions (no test framework setup needed from frontend).

## 2026-06-26 — Access Profiles UX Complete + Vitest Foundation

**Work Completed:** Access Profiles `/access` page layout enhancement complete with sticky positioning, policy search, and override filter. Filter state/logic ownership moved to page per McNulty's architectural gate. Pure filter logic extracted to `filtering.ts` module for testing + reusability.

**Key Pattern Established:** Page owns state + business logic transformation; component receives pre-computed filtered data + callbacks; rendering logic stays in component. This separation unblocks Bunk's unit tests (pure functions) and future features (URL state, saved presets, bulk operations).

**Team-Wide Context — Vitest is Now the Frontend Test Standard:**
- **Framework:** Vitest v4.1.9 (Vite-native, fast, React 19 compatible)
- **Test Location:** `src/components/accessProfiles/filtering.test.ts` (35 passing tests)
- **Future:** Component tests for ProfileGrid.tsx when JSX rendering needs validation (requires jsdom environment)
- **Pattern:** Extract pure functions to `.ts` modules; test independently via Vitest; import into components for use

**Cross-Team Note:** Vitest infrastructure now in place. Future frontend features should follow this pattern: pure logic in dedicated modules, components as thin rendering layers, tests for pure functions. Component-level testing can follow when needed (jsdom + @testing-library/react are already installed).