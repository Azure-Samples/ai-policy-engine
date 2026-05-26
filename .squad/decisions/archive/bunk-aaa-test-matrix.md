# Bunk AAA Access Profile Test Matrix

Date: 2026-05-21

## Scope delivered

Implemented the requested 21-test AAA anticipatory matrix for Access Profiles:

- Resolver unit tests: 6
- Precheck integration tests: 6
- Log integration tests: 4
- End-to-end cascade integration test: 1
- Pending M4 template assertions: 4 skipped

## Files

- `src/AIPolicyEngine.Tests/Services/AccessProfiles/AccessProfileTestSupport.cs`
- `src/AIPolicyEngine.Tests/Services/AccessProfiles/AccessProfileResolverTests.cs`
- `src/AIPolicyEngine.Tests/Integration/AccessProfilePrecheckTests.cs`
- `src/AIPolicyEngine.Tests/Integration/AccessProfileLogTests.cs`
- `src/AIPolicyEngine.Tests/Integration/AccessProfileCascadeE2ETests.cs`

## Matrix coverage

### Resolver unit tests (6)

1. Operation-specific profile beats API-wide profile
2. API-wide profile beats client-global profile
3. Client-global profile beats legacy assignment
4. No profiles returns legacy assignment
5. No profiles and no legacy assignment returns null
6. Disabled profile is skipped during cascade

### Precheck integration tests (6)

1. `apiId` + `operationId` path invokes resolver and returns `planId` + `accessProfileId`
2. No `apiId` uses legacy path and omits access-profile metadata
3. `apiId` with no matching profile falls back to legacy assignment
4. Disabled operation profile yields API-level profile
5. AAA response carries `allowedDeployments`
6. Legacy callers keep the current authorized response contract

### Log integration tests (4)

1. `PlanId` in request drives plan lookup
2. Missing `PlanId` falls back to `ClientPlanAssignment.PlanId`
3. `AccessProfileId` is written to the audit item
4. Legacy payload with no new AAA fields still works

### End-to-end cascade test (1)

1. Precheck resolves in order: operation -> API -> global -> legacy

### Pending M4 template assertions (4 skipped)

1. Template extracts `apiId`
2. Precheck URL carries `apiId` and `operationId`
3. Outbound log payload carries `accessProfileId`, `planId`, `apiId`, and `operationId`
4. Template manifest/version bumps to `1.1`

## Contract issues / decisions surfaced

1. **Resolver result type naming**
   - Architecture appendix referenced `ResolvedAccess`.
   - Implementation and approved tester contract use `ResolvedAccessProfile`.
   - Current code aligns to `ResolvedAccessProfile` and keeps `SourceProfileId` as an alias of `AccessProfileId` for compatibility.

2. **Legacy fallback ownership**
   - Appendix prose suggested the resolver internally falls through to legacy `ClientPlanAssignment`.
   - Sample precheck pseudocode still implied fallback might happen in the endpoint.
   - Current implementation resolves legacy fallback inside `AccessProfileResolver`, which matches the approved test matrix.

3. **Precheck additive contract**
   - AAA path returns additive fields (`planId`, `accessProfileId`, `allowedDeployments`) without breaking existing success fields.
   - Legacy precheck path still avoids access-profile metadata, preserving older callers.

4. **Log plan-resolution edge case**
   - The addendum discussed a possible mismatched-`planId` fallback-to-legacy case.
   - The approved 21-test matrix did not require that behavior, so it is not asserted here.
   - Tests cover the explicit contract only: supplied `PlanId` wins, otherwise legacy assignment wins.

## Validation

Ran:

`dotnet test src\AIPolicyEngine.Tests\AIPolicyEngine.Tests.csproj --no-restore --nologo`

Result:

- Total: 320
- Succeeded: 312
- Failed: 0
- Skipped: 8

AAA-specific result:

- Total: 21
- Succeeded: 17
- Skipped: 4 pending M4
- Failed: 0
