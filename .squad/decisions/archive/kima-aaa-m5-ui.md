# Kima AAA M5 — Access Profile admin UI

## Scope completed
- Added a new `/access` admin page for Access Profile management in the React/Vite SPA.
- Implemented a client-first workflow: searchable client list on the left, access matrix on the right.
- Visualized all three direct override scopes plus fallback inheritance:
  - client-global (`_global`)
  - API-wide
  - operation-level
- Added direct CRUD flows against `/api/access-profiles`:
  - list by client
  - point-read before edit
  - create
  - update
  - delete
- Added bulk-create flow using `/api/access-profiles/bulk` for queued inherited cells.
- Added a shared `useApimCatalog` hook and migrated both `/access` and `/apis` to reuse the same APIM catalog loading pattern.
- Added an `Access` nav item and route wiring in the shell.
- Extended the shared dialog primitive with `contentClassName` so the profile editor can render as a larger drawer-style panel.

## UI behavior notes
- Empty cells do not look blank; they show the currently effective cascade result before an override is created.
- Direct overrides are visually distinct from inherited values.
- Disabled direct profiles stay visible but are treated as non-winning, matching backend cascade behavior.
- API cards lazy-load operations from APIM when expanded.
- The editor supports both single-scope editing and bulk creation across queued scopes.
- The APIM `/apis` page now uses the same shared catalog hook, avoiding duplicate API/operation loading logic and preserving the render-loop-safe ref/callback pattern.

## Contract and architecture alignment
- The UI follows McNulty's client-first `/access` recommendation from `mcnulty-aaa-per-client-arch.md`.
- Access Profile IDs and scope modeling assume the shipped backend contract:
  - `ap:{clientAppId}:{tenantId}:{apiId}:{operationId|_all}`
  - `_global` for client-global defaults
- Effective state shown in the grid mirrors backend precedence:
  1. operation override
  2. API-wide override
  3. client-global override
  4. client plan assignment fallback
- Effective routing and allowed deployments mirror backend fallback semantics:
  - null routing policy falls back to the selected plan/default assignment
  - empty deployment list falls back to the selected plan/default assignment

## Validation
- `cd src\aipolicyengine-ui && npm run lint`
- `cd src\aipolicyengine-ui && npm run build`
- Result: both passed after the shared-hook `/apis` refactor was completed.

## Files changed
- `src\aipolicyengine-ui\src\App.tsx`
- `src\aipolicyengine-ui\src\components\Layout.tsx`
- `src\aipolicyengine-ui\src\components\ui\dialog.tsx`
- `src\aipolicyengine-ui\src\hooks\useApimCatalog.ts`
- `src\aipolicyengine-ui\src\api\accessProfiles.ts`
- `src\aipolicyengine-ui\src\types\accessProfiles.ts`
- `src\aipolicyengine-ui\src\pages\AccessProfiles.tsx`
- `src\aipolicyengine-ui\src\pages\Apis.tsx`
- `src\aipolicyengine-ui\src\components\accessProfiles\CascadeBadge.tsx`
- `src\aipolicyengine-ui\src\components\accessProfiles\ClientList.tsx`
- `src\aipolicyengine-ui\src\components\accessProfiles\ProfileEditor.tsx`
- `src\aipolicyengine-ui\src\components\accessProfiles\ProfileGrid.tsx`
- `src\aipolicyengine-ui\src\components\accessProfiles\types.ts`
