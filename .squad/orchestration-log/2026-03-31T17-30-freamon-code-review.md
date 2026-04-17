# Freamon — Code Review Fixes (B1, S1, S2, S3, S4, S7)

**Agent:** Freamon (Backend Dev)  
**Mode:** background  
**Model:** claude-sonnet-4.5  
**Date:** 2026-03-31T17:30:00Z  
**Review Findings Assigned:** B1, S1, S2, S3, S4, S7  

## Outcome: SUCCESS ✅

All 6 findings fixed. Build clean. 198/198 tests pass.

### Fixes Implemented

1. **B1 — Precheck multiplier request quota enforcement**
   - Added quota check in `PrecheckEndpoints.cs` after token quota logic
   - Returns 429 when effective requests exceed monthly quota and overbilling is disabled
   - Activates only when `UseMultiplierBilling` is true and quota > 0

2. **S1 — Deleted dead Repositories/ directory**
   - Removed duplicate code: `IRepository.cs`, `CachedRepository.cs`, `CacheWarmingService.cs`, `RedisToCosmossMigrationService.cs`
   - Updated test references from `Repositories` namespace to `Services`
   - Removed 4 dead test files (tests for deleted interfaces)
   - Net test result: -22 tests (deletion of tests for dead code)

3. **S2 — APIM JSON injection fix**
   - `policies/entra-jwt-policy.xml` — replaced string interpolation with JObject construction
   - `policies/subscription-key-policy.xml` — replaced string interpolation with JObject construction
   - Eliminates injection risk from JWT claims or subscription names

4. **S3 — ConfigurationContainerProvider race condition fix**
   - Replaced `volatile bool _initialized` with `SemaphoreSlim(1,1)` double-check locking
   - Safe under concurrent `EnsureInitializedAsync` calls

5. **S4 — Persist RoutingPolicyId in audit trail**
   - Both APIM policies: Added `routingPolicyId` extraction from precheck response
   - `LogIngestRequest.cs` — Added `RoutingPolicyId` property
   - `LogIngestEndpoints.cs` — Passes routing policy ID to audit log instead of null

6. **S7 — ChargebackCalculator pricing cache thread safety**
   - Added `_cacheLock` object with double-check locking pattern
   - Prevents stampede while keeping I/O non-blocking

### Test Results

**Before:** 220/220 tests pass  
**After:** 198/198 tests pass  
**Change:** -22 tests (all from deleted Repositories test files testing dead interfaces)  
**Regressions:** 0  

All core functionality tests pass. Dead code removed. Build clean (no warnings).

### Files Modified

- `src/Chargeback.Api/Endpoints/PrecheckEndpoints.cs`
- `src/Chargeback.Api/Services/ChargebackCalculator.cs`
- `src/Chargeback.Api/Services/ConfigurationContainerProvider.cs`
- `src/Chargeback.Api/Models/LogIngestRequest.cs`
- `src/Chargeback.Api/Endpoints/LogIngestEndpoints.cs`
- `policies/entra-jwt-policy.xml`
- `policies/subscription-key-policy.xml`

### Files Deleted

- `src/Chargeback.Api/Repositories/IRepository.cs`
- `src/Chargeback.Api/Repositories/CachedRepository.cs`
- `src/Chargeback.Api/Repositories/CacheWarmingService.cs`
- `src/Chargeback.Api/Repositories/RedisToCosmossMigrationService.cs`
- `src/Chargeback.Tests/CachedRepositoryTests.cs`
- `src/Chargeback.Tests/CacheWarmingServiceTests.cs`
- `src/Chargeback.Tests/RedisToCosmossMigrationServiceTests.cs`
- Test methods removed from `CosmosPersistenceResilienceTests.cs` (4 integration tests)

### Status: READY FOR MERGE
