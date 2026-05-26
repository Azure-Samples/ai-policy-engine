# Decision Inbox: Non-AI API Limits Test Coverage Strategy

**Date:** 2026-05-21  
**By:** Bunk (Tester / QA)  
**Status:** Proposed

## What

Draft non-AI API usage-limit coverage in three layers once implementation lands:

1. **Endpoint tests first** in `src/AIPolicyEngine.Tests/EndpointTests.cs` for plan CRUD validation, basic non-AI precheck allow/deny behavior, and AI-regression checks.
2. **Focused integration tests** in a new non-AI-specific integration class for counter isolation, cache/Cosmos round-trip, rollover behavior, and failure-mode assertions.
3. **NBomber load coverage** by extending `src/AIPolicyEngine.LoadTest/Program.cs` with a high-throughput non-AI precheck scenario across multiple customer keys.

## Why

This matches the repository's existing testing layout: endpoint behavior in `EndpointTests`, scenario-heavy integration logic under `Integration/`, and performance verification in the dedicated load-test project. It also keeps the draft adaptable while architecture is still being finalized, because endpoint names and body shapes can change without forcing a rewrite of the overall coverage strategy.

## Open Dependencies

- Final sign-off on the non-AI endpoint contract and schema fields.
- Clear semantics for `0` values, rejected-request counter behavior, and quota reset rules.
- Prefer a clock seam/time provider so rollover tests do not rely on real waits.
