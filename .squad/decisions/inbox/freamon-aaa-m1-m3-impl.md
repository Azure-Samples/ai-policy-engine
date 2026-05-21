# Freamon AAA Access Profiles M1-M3 implementation

## Scope delivered
- Added `AccessProfile` data model and Cosmos-backed repository on the shared `configuration` container with partition key `access-profile` and deterministic IDs `ap:{clientAppId}:{tenantId}:{apiId}:{operationId|_all}`.
- Added `IAccessProfileResolver` + resolver cascade for operation-specific, API-wide, client-global, then legacy client assignment fallback.
- Added admin CRUD and bulk endpoints under `/api/access-profiles` guarded by `AdminPolicy`.
- Integrated access-profile-aware precheck behavior with optional `apiId`, `operationId`, and additive response fields `planId`, `accessProfileId`, and `allowedDeployments`.
- Extended log-ingest contracts to accept optional `accessProfileId`, `planId`, `apiId`, and `operationId`, and persisted those fields to the audit stream.

## Key implementation decisions
- Kept client metering on `ClientPlanAssignment`; access profiles resolve plan/routing/deployment policy, but precheck still requires a client assignment for quota/rate-limit state so log-ingest and precheck stay aligned.
- Preserved legacy precheck callers when `apiId` is absent, while still surfacing `planId` for additive contract compatibility.
- Used plan inheritance semantics for `routingPolicyId` and `allowedDeployments`: access-profile overrides win when populated; otherwise plan defaults apply.
- Did not touch APIM template XML or UI code.

## Main files
- `src/AIPolicyEngine.Api/Models/AccessProfile*.cs`
- `src/AIPolicyEngine.Api/Services/AccessProfiles/*`
- `src/AIPolicyEngine.Api/Endpoints/AccessProfileEndpoints.cs`
- `src/AIPolicyEngine.Api/Endpoints/PrecheckEndpoints.cs`
- `src/AIPolicyEngine.Api/Endpoints/LogIngestEndpoints.cs`
- `src/AIPolicyEngine.Api/Models/LogIngestRequest.cs`
- `src/AIPolicyEngine.Api/Models/AuditLogItem.cs`
- `src/AIPolicyEngine.Api/Models/AuditLogDocument.cs`
- `src/AIPolicyEngine.Api/Services/AuditLogWriter.cs`
- `src/AIPolicyEngine.Api/Program.cs`

## Validation
- `dotnet build src\AIPolicyEngine.Api\AIPolicyEngine.Api.csproj --nologo`
- `dotnet test src\AIPolicyEngine.Tests\AIPolicyEngine.Tests.csproj --no-restore --nologo`
- Test run passed locally: 311 succeeded, 8 skipped.
