# Squad Decisions

## Overview

This file maintains the team's active architectural decisions and recent implementation records. Decisions older than 30 days are archived to `.squad/decisions-archive-*.md` to keep this file focused and under 20KB.

For foundational architectural decisions, design patterns, and full implementation records from the first 8 weeks (2026-03-31 to 2026-05-26), see `.squad/decisions-archive-20260626.md`.

## Recent Decisions

### 2026-06-26 — Access Profiles Layout Enhancement — Sticky Offsets & Accessibility

**Owner:** Kima (Frontend Developer)  
**Status:** Implemented  
**Date:** 2026-06-26  

### Sticky Positioning Rule

**Always match sticky `top-*` offset to the exact header height.**

- Header: `h-16` (4rem = 64px)
- Sticky elements: `top-16` (not `top-[5.5rem]` or arbitrary pixel values)
- Pinned viewport height: `h-[calc(100vh-4rem)]` (using the same offset value)

**Rationale:** Mismatched offsets create visual gaps or overlaps. Using the same Tailwind class for both header height and sticky offset ensures they stay synchronized.

### Accessibility Requirements for Search & Filter Controls

1. **Search inputs with icon-only labels:**
   - Must include `aria-label` (placeholders are not accessible labels)
   - Example: `<Input aria-label="Search access profiles" placeholder="Search by API…" />`

2. **Filter button groups:**
   - Wrap in a container with `role="group"` and `aria-label`
   - Each toggle button must have `aria-pressed={boolean}` to announce state

**Files Affected:** AccessProfiles.tsx, ClientList.tsx, ProfileGrid.tsx

**Why:** WCAG 2.1 accessibility compliance; Tailwind class synchronization prevents future header-height changes from breaking sticky positioning.

---

### 2026-06-26 — Access Profiles Filter Logic Separation of Concerns (Code Review Gate)

**Owner:** McNulty (Lead/Architect)  
**Status:** Approved  
**Reviewer:** McNulty  
**Date:** 2026-06-26  

### Verdict: REQUEST CHANGES → APPROVED (After Refactor)

Initial commit `862fc5d5` introduced 170 lines of filter state and business logic in `ProfileGrid.tsx` (presentation component). Per established pattern, data transformation belongs in the page layer, not the component.

### Required Refactor (Implemented by Kima)

1. **Filter state moved to AccessProfiles.tsx:** `searchQuery`, `overrideFilter` state now owned by page
2. **Filter logic extracted to pure module:** New `src/components/accessProfiles/filtering.ts` with:
   - `cellMatchesSearch(cell, query, plansById)` — Case-insensitive search across 7 fields
   - `cellMatchesOverride(cell, filter)` — Three modes (all/overrides/inherited)
   - `selectFilteredView(...)` — Section visibility logic with edge case handling
   - `OVERRIDE_FILTERS` type definitions
3. **ProfileGrid receives pre-filtered data:** Component now rendering-only, no filter logic

### Why This Pattern Matters

- **Consistency:** Aligns with existing page-owns-state, component-renders pattern (expand/collapse already follows this)
- **Testability:** Filter logic testable in isolation without component mounting
- **Future Features:** Saved filter presets, URL-based filters, bulk operations all now viable without refactoring
- **State Synchronization:** Eliminates duplication risks between page and grid state

### Architecture Alignment

Per M5 spec (2026-05-21): `/access` page owns data transformation; ProfileGrid owns presentation. This pattern ensures clean separation and consistent maintainability across all admin pages (Plans, Routing, APIs).

---

### 2026-06-26 — Frontend Test Framework Decision (Quality Gate)

**Owner:** Bunk (Tester/QA)  
**Status:** Approved & Implemented  
**Date:** 2026-06-26  

### Problem

The frontend (`src/aipolicyengine-ui`) had ZERO test framework configured. Commit `862fc5d5` introduced significant user-facing filter logic:
- `cellMatchesSearch` — Case-insensitive search across 7 fields (API/operation/plan/method/path)
- `cellMatchesOverride` — Three filter modes with directProfile presence checks
- Section visibility + visibleScopeCount — Complex memo logic
- Empty state rendering

**Quality Gate Failure:** 0% coverage on production-grade logic.

### Decision: Adopt Vitest for Frontend Testing

**Framework Choice:** Vitest v4.1.9
- **Why:** Vite-native (zero-config), React 19 compatible, fast test execution (~7ms for 35 tests), industry standard for Vite projects
- **Additional Packages:** @testing-library/react (component testing future), @testing-library/jest-dom, jsdom (browser env), @vitest/ui (optional)
- **Config:** `vitest.config.ts` (node environment for pure functions; jsdom available for component tests)

### Implementation

**Commit:** `cdebab40`  
- Created `vitest.config.ts` with test configuration
- Created `src/components/accessProfiles/filtering.test.ts` — 35 unit tests
- Added `npm test` (CI mode) and `npm run test:watch` (dev mode) scripts

### Test Coverage

**35 Passing Tests (100% of filtering.ts):**
- `cellMatchesSearch` — 11 tests (empty query, case-insensitive matching, null planName handling, field coverage)
- `cellMatchesOverride` — 3 tests (all/overrides/inherited modes)
- `selectFilteredView` — 21 tests (section visibility, override filtering, visibleScopeCount, edge cases)

**All 10 Original Priority Cases — COVERED**

### Validation

✅ All 35 tests passing  
✅ TypeScript clean (`tsc -b`)  
✅ Vite build successful  

### Future Scope

- Component tests for ProfileGrid.tsx (switch vitest.config to jsdom)
- Coverage reporting script for CI pipelines
- Pre-commit hooks (husky + lint-staged)
- GitHub Actions CI integration

**Key Decision:** Vitest is now the established frontend test runner for aipolicyengine-ui.

---

## Governance

- All meaningful changes require team consensus
- Archive decisions older than 30 days to `.squad/decisions-archive-*.md`
- Document architectural decisions for team memory
- Keep history focused on work, decisions focused on direction
**By:** Scribe (logged from orchestration)  
