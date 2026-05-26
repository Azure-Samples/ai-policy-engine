# Sydnor AAA M4 — APIM template XML updates

## Scope completed
- Updated all 5 shipped APIM templates under `policies/templates/*/policy.xml` for AAA access-profile metadata propagation.
- Added `apiIdValue` / `operationIdValue` capture to every template.
- Updated the 4 AI templates (`entra-jwt-ai`, `entra-jwt-ai-dlp`, `subscription-key-ai`, `subscription-key-ai-dlp`) to append `apiId` + `operationId` to precheck calls and extract `accessProfileId` + `planId` from the precheck response.
- Updated outbound log payloads to carry `accessProfileId`, `planId`, `apiId`, and `operationId` using the existing lower-camel JSON payload convention.
- Updated `entra-jwt-rest` per McNulty's matrix: active outbound log payload now carries the new AAA fields; the commented `precheck-rest` alternative also shows the api/operation query params plus response-field extraction for future activation.
- Bumped all 5 template manifests from version `1.0` to `1.1`.
- Activated Bunk's 4 pending M4 tests in `AccessProfilePrecheckTests` and replaced the placeholders with concrete assertions against the shipped template files.

## Contract alignment notes
- Precheck response field names were taken from Freamon's shipped contract: `planId`, `accessProfileId`, `allowedDeployments`.
- Outbound log payload additions use lower-camel JSON property names (`accessProfileId`, `planId`, `apiId`, `operationId`) to match the existing APIM payload style and the endpoint contract.
- Used local APIM variable names `apiIdValue` / `operationIdValue` to avoid ambiguity with JSON property names while still emitting `apiId` / `operationId` on the wire.
- Kept AI-template response extraction variable as `resolvedPlanId` so the log payload can cleanly map to `planId` and the audit model continues to distinguish resolved-plan metadata from legacy assignment fallback.

## Validation
- `dotnet test src\AIPolicyEngine.Tests\AIPolicyEngine.Tests.csproj --no-restore --nologo`
- Result: **320 total / 316 passed / 0 failed / 4 skipped**
- Remaining skips are the pre-existing Purview seam tests; the 4 prior M4 template skips are now active and passing.

## Files changed
- `policies/templates/entra-jwt-ai/policy.xml`
- `policies/templates/entra-jwt-ai/template.json`
- `policies/templates/entra-jwt-ai-dlp/policy.xml`
- `policies/templates/entra-jwt-ai-dlp/template.json`
- `policies/templates/subscription-key-ai/policy.xml`
- `policies/templates/subscription-key-ai/template.json`
- `policies/templates/subscription-key-ai-dlp/policy.xml`
- `policies/templates/subscription-key-ai-dlp/template.json`
- `policies/templates/entra-jwt-rest/policy.xml`
- `policies/templates/entra-jwt-rest/template.json`
- `src/AIPolicyEngine.Tests/Integration/AccessProfilePrecheckTests.cs`
