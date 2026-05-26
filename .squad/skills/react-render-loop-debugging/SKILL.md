---
name: "react-render-loop-debugging"
description: "Diagnose and fix React render loops caused by unstable effect or callback dependencies"
domain: "frontend, react, debugging"
confidence: "high"
source: "earned — from fixing the APIM /apis page infinite render loop"
---

## Context
Use this skill when a React page keeps re-rendering, refetching, or polling forever after mount or selection changes.

## Patterns
1. Trace the full effect chain: `useEffect` -> callback/hook -> `setState` -> dependency churn.
2. If a callback is invoked by a mount effect, do not make that callback depend on maps, arrays, or objects that the callback also resets or replaces.
3. Use refs to read the latest mutable collections inside async callbacks when you need current reconciliation data without changing callback identity.
4. Prefer stable IDs/strings in polling and fetch effects over whole selected objects.
5. Check retry toasts and polling callbacks for recursive calls that capture unstable objects.

## Examples
- `src/aipolicyengine-ui/src/pages/Apis.tsx`: `loadInitialData` originally depended on `operationsByApi` and also called `setOperationsByApi({})`, so the mount effect keyed to `loadInitialData` re-fired continuously.
- Fix pattern: mirror `operationsByApi` into `operationsByApiRef.current` and read the ref inside the callback, leaving the callback deps stable.

## Anti-Patterns
- `useEffect(() => { void loadInitialData() }, [loadInitialData])` when `loadInitialData` depends on state it also recreates.
- Depending on inline objects/arrays or selected entity objects when only an ID is needed.
- Polling effects that re-arm on every render because status inputs are not stable.
