# Decision: Agent365 Observability Test Coverage

**Date:** 2026-04-11  
**Author:** Bunk (Tester)  
**Status:** Complete  
**Context:** Freamon added Agent365 Observability SDK integration with InvokeAgent and Inference scope instrumentation

## What Was Added

Freamon implemented Agent365 Observability integration:
- `IAgent365ObservabilityService` interface (StartInvokeAgentScope, StartInferenceScope → IDisposable?)
- `Agent365ObservabilityService` — real impl (currently stubs returning null pending SDK API stabilization v0.1.75-beta)
- `NoOpAgent365ObservabilityService` — always returns null (disabled state)
- `Agent365ServiceExtensions.AddAgent365Observability()` — DI registration with `ENABLE_A365_OBSERVABILITY_EXPORTER` toggle
- Wired into `PrecheckEndpoints.cs` (Precheck + ContentCheck) and `LogIngestEndpoints.cs`

## Tests Added

Created new test file `src/Chargeback.Tests/Agent365ServiceTests.cs` with 10 unit tests:

### NoOpAgent365ObservabilityService (2 tests)
1. `NoOpAgent365ObservabilityService_StartInvokeAgentScope_ReturnsNull` — verify stub returns null
2. `NoOpAgent365ObservabilityService_StartInferenceScope_ReturnsNull` — verify stub returns null

### DI Registration (4 tests)
3. `AddAgent365Observability_WithoutConfig_RegistersNoOp` — no env var → NoOp registered
4. `AddAgent365Observability_WithFalseConfig_RegistersNoOp` — `ENABLE_A365_OBSERVABILITY_EXPORTER=false` → NoOp
5. `AddAgent365Observability_WithInvalidConfig_RegistersNoOp` — invalid bool → NoOp (safe default)
6. `AddAgent365Observability_WithExporterEnabled_RegistersRealService` — `ENABLE_A365_OBSERVABILITY_EXPORTER=true` → real service

### Agent365ObservabilityService Stub Implementation (4 tests)
7. `Agent365ObservabilityService_StartInvokeAgentScope_ReturnsNullStub` — verify stub returns null without crash
8. `Agent365ObservabilityService_StartInferenceScope_ReturnsNullStub` — verify stub returns null without crash
9. `Agent365ObservabilityService_StartInvokeAgentScope_WithNullOptionalParams_ReturnsNullStub` — null optional params → no crash
10. `Agent365ObservabilityService_StartInferenceScope_WithNullDisplayName_ReturnsNullStub` — null clientDisplayName → no crash

## Design Rationale

### Why Test Stubs?

The current implementation is a **stub** returning null because the Agent365 SDK (v0.1.75-beta) API surface is still evolving. Once the SDK stabilizes with documented scope creation APIs (InvokeAgentScope.Start, InferenceScope.Start), the service will be updated.

Testing stubs validates:
1. **Safe defaults** — NoOp service never throws, real service stubs return null gracefully
2. **DI registration** — env var toggle correctly routes to NoOp vs real service
3. **Null safety** — optional parameters (clientDisplayName, correlationId, promptContent) can be null without crashes
4. **Resilience** — disabled observability (NoOp) is a valid production state

### Why Separate Test File?

- **Clarity:** `Agent365ServiceTests.cs` is distinct from `PurviewServiceTests.cs` — different services, different concerns
- **Namespace isolation:** Agent365 has no overlapping types with Purview (unlike Repositories/Services namespace collision)
- **Team coordination:** Easier for Freamon to find related tests when updating the implementation

### When Real SDK Lands

When Agent365 SDK stabilizes, tests should be extended to:
1. Verify `IDisposable` scopes are returned (not null)
2. Verify scope lifecycle (using/Dispose patterns)
3. Verify trace/span metadata (ActivitySource spans, attributes)
4. Verify scope nesting (InvokeAgent → Inference)

Current tests will remain valid — they verify the fail-safe behavior (null returns don't crash callers).

## Edge Cases Covered

- **Invalid config:** Non-boolean env var values default to NoOp (safe fallback)
- **Null parameters:** clientDisplayName, correlationId, promptContent can be null
- **Disabled state:** NoOp service is always safe (no network calls, no exceptions)
- **Stub returns:** Real service stubs return null without throwing (pre-SDK API)

## Impact

- **Test count:** 221 → 231 (10 new tests, all passing)
- **Coverage:** Agent365 integration now has baseline test coverage for DI registration and stub behavior
- **Risk mitigation:** Tests verify safe-defaults and null-safety before real SDK instrumentation lands

## Future Work

When Agent365 SDK API stabilizes:
1. Update `Agent365ObservabilityService` to call `InvokeAgentScope.Start()` and `InferenceScope.Start()`
2. Add tests for non-null IDisposable scopes
3. Add tests for ActivitySource span creation (if using OpenTelemetry)
4. Add tests for scope nesting and parent-child relationships
5. Consider integration tests with real ActivityListener to verify trace propagation

Current stub tests will remain as regression coverage for the null-safe fallback path.
