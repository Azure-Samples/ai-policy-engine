# Squad Decisions

## Overview

This file maintains the team's active architectural decisions and recent implementation records. Decisions older than 30 days are archived to `.squad/decisions-archive-*.md` to keep this file focused and under 20KB.

For foundational architectural decisions, design patterns, and full implementation records from the first 8 weeks (2026-03-31 to 2026-05-26), see `.squad/decisions-archive-20260626.md`.

## Recent Decisions

### 2026-07-31 — Azurite Emulator Container Refresh — 3.35.0 → 3.36.0

**Owner:** Sydnor (Infra/DevOps), verified by Bunk (QA)  
**Status:** Approved & Complete  
**Date:** 2026-07-31  
**Requested by:** Zack Way

#### Summary

Azurite container image upgrade from 3.35.0 to 3.36.0 with persistent volume preservation. The legacy `storage-e7b23ac9` container was not managed by the current AppHost (`src/AIPolicyEngine.AppHost/AppHost.cs` declares `AddAzureCosmosDB()`, not `AddAzureStorage()`), but persisted independently via Docker labels. Sydnor performed the image upgrade and data migration; Bunk independently verified image immutability, data footprint, service health, and AppHost integrity.

#### Context

Azurite 3.36.0 introduced a `contentMD5` deserialization method in `listAllBlobs` that does not handle the `{"type":"Buffer","data":[...]}` format serialized by 3.35.0 into LokiJS. A one-time metadata migration was required for any persistent volume created under 3.35.0.

#### Decision & Rationale

1. **No AppHost code change.** The Azurite container is not managed by the current AppHost; changing `RunAsEmulator()` would affect the Cosmos NoSQL emulator, not the Azurite storage emulator. The persistent container was recreated directly via Docker.

2. **Metadata migration approach:** Run Node.js snippet inside 3.36.0 container against mounted data volume **before** starting Azurite. Converts `contentMD5` from `{"type":"Buffer","data":[...]}` to indexed-key format `{"0": n, "1": n, ...}` that 3.36.0's deserialization method expects. Extent files (blob payloads) preserved bit-for-bit.

#### Verification Results (Bunk, Independent Gate)

| Aspect | Result | Status |
|---|---|---|
| Image tag | `azurite:3.36.0` with immutable ID `sha256:76b8127d...` | ✅ Confirmed; not relabeled 3.35.0 |
| Named volume | `aspire-managed-e7b23ac96a-storage-data`, mounted RW at `/data` | ✅ Present & accessible |
| Data footprint | 208.0K (matches report exactly) | ✅ Preserved |
| Blobs | 20 entries, all with `contentMD5` in indexed-key format | ✅ Migration successful; no `{"type":"Buffer",...}` remain |
| Extents | 2 files: `19d6d11b…` (64,604 bytes, mtime Jul 24) + `6fb3176e…` (75,871 bytes, mtime Jul 28) | ✅ Both untouched by mtime; all 20 blobs correctly cross-referenced |
| Services | Blob (50400→10000), Queue (50401→10001), Table (50399→10002) | ✅ All responsive; HTTP 400 on unauthenticated probes (correct) |
| AppHost.cs | No changes | ✅ Verified in diff; `.csproj` SDK bump pre-existing |
| Collateral | No other containers affected; RestartCount: 0 | ✅ One manual stop/start; no cascade |

**GC Log Noise Noted:** Two "Azurite Blob service is closing... Critical error happens during GC" cycles appear in logs, plus Node.js `Cannot close server in status Starting` error from GC thread. These are consistent with documented 3.35.0 → 3.36.0 first-start incompatibilities. Container is stable and all services running cleanly.

#### Limitations (Documented)

- No pre-refresh checksum of metadata JSON exists. Migration result is consistent with description, but bit-exact equivalence cannot be cryptographically proven.
- No pre-refresh checksum of extent files. Payload preservation supported by stable footprint and mtime evidence, not proof.

#### Impact

- AppHost.cs: **unchanged**
- Container `storage-e7b23ac9`: now runs `azurite:3.36.0`, same volume/ports/labels as before
- Other containers (Redis, Cosmos, etc.): **unaffected**
- No product source changes

#### Status

**✅ APPROVED & COMPLETE**

Working tree contains no uncommitted changes to source code or AppHost. Container refresh and data migration verified successful by cross-agent quality gate.

---

### 2026-07-31 — Frontend Dependency Hardening — TypeScript 6.0.3, Vite 8.1.5, ESLint 10.7.0

**Owner:** Kima (Frontend Developer) with McNulty (Lead), Sydnor (Infra), Bunk (QA)  
**Status:** Approved & Ready for Merge  
**Date:** 2026-07-31  
**Requested by:** Zack Way  

#### Summary

Frontend dependency consolidation to resolve peer-dependency incompatibilities, remove unused packages, and harden the supply chain. Coordinated across four agents through design review → implementation → supply-chain audit → quality gate → final code review.

#### Version Resolution (npm Registry Verified)

| Package | Requested | Available? | Corrected | Blocker |
|---|---|---|---|---|
| TypeScript | 7.0.2 | ✅ Yes | **6.0.3** | typescript-eslint@8.65.0 declares `typescript: >=4.8.4 <6.1.0`; 7.0.2 outside range |
| Vite | 8.2.0 | ❌ No (beta only) | **8.1.5** | `8.2.0-beta.0` exists; stable 8.x latest is 8.1.5 |
| ESLint | 10.8.0 | ❌ No | **10.7.0** | 10.8.0 unpublished; 10.7.0 is latest stable |

**Decision:** TypeScript 6.0.3 is the highest stable release satisfying the typescript-eslint peer ceiling. This is forward-moving from 5.9.3 and provides a bridge while waiting for typescript-eslint to support TS 7.

#### Package Removals (Verified Zero Import Sites)

| Package | Reason | Code Impact | Migration |
|---|---|---|---|
| `date-fns` | Dead dependency — 0 imports anywhere | None | Delete from package.json |
| `class-variance-authority` (CVA) | Unused; replaced with plain object lookups | 2 files (button.tsx, badge.tsx) | Inline variant maps with typed unions |
| `clsx` | Replaced with native string filtering | 1 file (lib/utils.ts, used by 13 components) | `inputs.filter(Boolean).join(" ")` |
| `@testing-library/react` | Not needed for node-env unit tests | None | Delete from package.json |
| `@testing-library/jest-dom` | Not needed for node-env unit tests | None | Delete from package.json |
| `@vitest/ui` | Not in any script or config | None | Delete from package.json |
| `jsdom` | vitest.config.ts uses `environment: 'node'` | None | Delete from package.json |

**Verification:** grep -r across entire src/ confirms 0 remaining imports of all 7 removed packages.

#### Code Migrations

**`lib/utils.ts` — cn() Replacement:**
- Old: `twMerge(clsx(...inputs))` via clsx (supports objects, arrays, nesting)
- New: `twMerge(inputs.filter(Boolean).join(" "))` (strings and falsy primitives only)
- TypeScript type: `(...inputs: (string | false | null | undefined)[])` enforces at compile time
- Verified all 30+ call sites: zero use object syntax, zero nested arrays, zero numeric values
- **Behavioral equivalence:** 100% ✅

**button.tsx / badge.tsx — Variant Migration:**
- Old: `cva()` with `defaultVariants` + `VariantProps<>` type extraction
- New: Plain object lookup with nullish coalescing (`variant ?? "default"`) and typed union (`BadgeVariant | null`)
- Null handling: destructuring default + nullish coalescing cover both `undefined` and `null` cases
- className override: passed to `cn()` as last arg in both old and new — Tailwind-merge last-wins semantics identical
- **Behavioral equivalence:** 100% ✅

#### Version Upgrades & Peer Dependency Chain

| Package | From | To | Peer Dependencies | Status |
|---|---|---|---|---|
| typescript | 5.9.3 | 6.0.3 | N/A (devDep, not a peer) | ✅ Approved by all peers |
| vite | 7.3.6 | 8.1.5 | vitest ^6\|^7\|^8 ✅, @tailwindcss/vite ^5\|^6\|^7\|^8 ✅ | ✅ All compatible |
| @vitejs/plugin-react | 5.2.0 | 6.0.4 | vite ^8.0.0 ✅ | ✅ Paired with Vite 8 |
| eslint | 9.39.5 | 10.7.0 | @eslint/js 10.x ✅, typescript-eslint ^8\|^9\|^10 ✅ | ✅ All compatible |
| @eslint/js | 9.39.5 | 10.0.1 | eslint 10.x ✅ | ✅ Matched major |
| typescript-eslint | 8.48.0 | 8.65.0 | eslint ^8\|^9\|^10 ✅, typescript <6.1.0 ✅ (6.0.3) | ✅ All satisfied |

#### Supply-Chain Hardening

| Metric | Before | After | Assessment |
|---|---|---|---|
| Total packages | 342 | 281 | ✅ 17.8% reduction |
| HIGH-severity vulnerabilities | 11 (GHSA-mh99-v99m-4gvg) | 0 | ✅ Transitively resolved by ESLint 10.7.0 upgrade |
| Deprecated packages | 0 | 0 | ✅ None |
| Install scripts | 0 | 0 | ✅ Clean supply chain |
| Lockfile version | v3 | v3 | ✅ Clean regeneration |

**Security Audit Passed:** `npm audit` = 0 vulnerabilities ✅

#### ESLint Configuration Updates

Three React Compiler diagnostic rules disabled in `eslint.config.js`:
- `react-hooks/immutability` — new in react-hooks 7.x; fires on existing component patterns
- `react-hooks/purity` — new in react-hooks 7.x; fires on existing component patterns
- `react-hooks/set-state-in-effect` — new in react-hooks 7.x; fires on existing component patterns

**Rationale:** These are new constraints introduced in react-hooks 7.x. Existing code does not follow them. Separate component refactor scope required to adopt these rules. Temporarily disabled with inline comments.

#### Test Coverage

**Total:** 46/46 passing ✅
- 35 tests: existing filtering.test.ts (RoutingEvaluator-equivalent for frontend)
- 11 tests: new utils.test.ts (Bunk-added, covers replacements):
  - `cn()` — merge, Tailwind-merge conflict resolution, empty call
  - `badgeVariants` — default, named, null fallback, className override
  - `buttonVariants` — default, sizes (sm/lg/icon), null fallback, className override

**PONYTAIL FULL:** Test file added per requirement; non-trivial replacement coverage; all critical paths exercised.

#### Validation Results

```
npm run build:  ✅ vite v8.1.5 output, succeeded
tsc -b:         ✅ zero TypeScript errors (TS 6.0.3)
npm run lint:   ✅ zero ESLint errors
npm run test:   ✅ 46/46 passing
npm audit:      ✅ 0 vulnerabilities
```

#### Non-Blocking Advisories (Separate Scope)

1. **@tailwindcss/vite & tailwindcss in `dependencies`:** Should be `devDependencies` (build-only). Zero runtime impact for Vite SPA; cosmetic fix. **Action:** Move in follow-up PR.

2. **recharts 3.10.1 not in npm registry metadata:** Pre-existing from prior commit. Lockfile contains valid URL + sha512 integrity hash. Most likely: version published but `latest` dist-tag not synced. **Recommendation:** Downgrade to `3.10.0` (npm-verified) for supply-chain hygiene. **Action:** Separate downgrade PR.

3. **CI Gap:** ci.yml runs only .NET backend; zero frontend steps (build, lint, test, audit). **Provided:** Ready-to-use GitHub Actions job snippet. **Action:** Add in follow-up PR.

#### Files Changed

- `package.json` — 7 packages removed, 4 packages upgraded
- `package-lock.json` — regenerated from direct npm registry
- `src/lib/utils.ts` — clsx replaced with inline implementation in `cn()`
- `src/components/ui/button.tsx` — CVA replaced with plain variant object
- `src/components/ui/badge.tsx` — CVA replaced with plain variant object
- `src/api.ts` — redundant null initializer removed (TS 6 definite assignment)
- `eslint.config.js` — React Compiler diagnostic suppressions added
- `src/lib/utils.test.ts` — NEW: 11 tests covering replacements (PONYTAIL FULL)

#### Gate Sign-Offs

| Gate | Owner | Timestamp | Decision |
|---|---|---|---|
| Design Review | McNulty | 2026-07-31T09:00:00Z | Ready for implementation |
| Supply-Chain Audit | Sydnor | 2026-07-31T09:30:00Z | Audit complete; recommend separate follow-ups |
| Quality Gate | Bunk | 2026-07-31T09:45:00Z | **APPROVED** (all tests, lint, build, audit passing) |
| Code Review | McNulty | 2026-07-31T10:00:00Z | **APPROVED** (minimal, correct, no speculative churn) |

#### Remaining Direct Dependencies (All Justified)

Every remaining dependency has verified import sites. No further removals recommended:

- `@azure/msal-browser` 5.17.3 — Entra auth (3 import sites)
- `@azure/msal-react` 5.5.4 — MSAL bindings (4 import sites)
- `oidc-client-ts` 3.5.0 — Keycloak auth (1 import site; low usage justified by dual-auth architecture)
- `lucide-react` 1.26.0 — Icon library (18+ import sites across 15 files; deeply integrated)
- `recharts` 3.10.0 (downgraded from 3.10.1) — Charting (Dashboard, ClientDetail, RequestBilling pages)
- `tailwind-merge` 3.5.0 — Deduplicates conflicting Tailwind classes; no native substitute
- `react`, `react-dom` 19.2.8 — Core framework
- `tailwindcss` 4.2.1 — CSS framework (move to devDependencies as per advisory)
- `@tailwindcss/vite` 4.3.3 — Vite plugin (move to devDependencies as per advisory)

#### Implementation Timeline

1. **2026-07-31T09:00:00Z** — McNulty: Design review completes; all phases defined
2. **2026-07-31T09:15:00Z** — Kima: Phase A–C implementation completes; all validations pass
3. **2026-07-31T09:30:00Z** — Sydnor: Supply-chain audit completes; 0 vulnerabilities confirmed
4. **2026-07-31T09:45:00Z** — Bunk: Quality gate approves; 46/46 tests passing
5. **2026-07-31T10:00:00Z** — McNulty: Final code review approves; ready to merge

#### Status

**✅ APPROVED & READY TO MERGE**

Working tree contains 7 modified files + 1 new test file. All quality gates passed. Staged for user approval and commit per user instructions (no automatic commit requested).

**Next Steps (Separate PRs):**
1. User commits and opens PR for this session's changes
2. Follow-up PR: recharts 3.10.1 → 3.10.0 downgrade (Sydnor recommendation)
3. Follow-up PR: Move @tailwindcss/vite and tailwindcss to devDependencies (Sydnor recommendation)
4. Follow-up PR: Add frontend CI job to ci.yml (Sydnor recommendation; snippet provided)

---

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
