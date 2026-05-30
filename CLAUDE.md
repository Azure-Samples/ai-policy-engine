# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Azure AI Gateway Policy Engine — a .NET 10 minimal API that acts as an enforcement layer between Azure API Management (APIM) and Azure OpenAI. It evaluates per-customer plans, routing policies, quotas, and rate limits on every AI request. A React/TypeScript dashboard provides management and observability.

## Commands

### Backend (.NET)

```bash
# Local dev with Aspire orchestration (spins up Redis, Cosmos, and API automatically)
dotnet run --project src/AIPolicyEngine.AppHost

# Run all tests
dotnet test src/AIPolicyEngine.Tests/AIPolicyEngine.Tests.csproj

# Run a single test class
dotnet test src/AIPolicyEngine.Tests/ --filter "FullyQualifiedName~RoutingEvaluatorTests"

# Performance benchmarks
dotnet run -c Release --project src/AIPolicyEngine.Benchmarks -- --filter "*Routing*"

# Load testing
dotnet run --project src/AIPolicyEngine.LoadTest

# Build release
dotnet build src/AIPolicyEngine.slnx --configuration Release
```

### Frontend (React SPA — `src/aipolicyengine-ui/`)

```bash
npm run dev       # Vite dev server (HMR on http://localhost:5173)
npm run build     # TypeScript check + Vite build → outputs to ../AIPolicyEngine.Api/wwwroot/
npm run lint      # ESLint
```

### Docker

```bash
docker build -t aipolicyengine:latest .
```

### Infrastructure

```bash
cd infra/terraform
terraform init && terraform apply -var-file="terraform.tfvars"
# Or use the one-shot PowerShell script:
./scripts/setup-azure.ps1
```

## Architecture

### Request Flow (Critical Path)

1. **Client → APIM**: Bearer JWT (Entra or Keycloak) validated at APIM layer
2. **APIM inbound policy → `/api/precheck/{clientAppId}/{tenantId}`**: Policy engine checks plan, access profiles, routing rules, quotas, rate limits. Returns routed deployment ID or 401/403/429.
3. **APIM → Azure OpenAI**: Forwards using managed identity with the resolved deployment
4. **APIM outbound policy → `/api/log`**: Fire-and-forget usage ingestion; updates quotas, calculates cost, emits metrics and audit events

### Key Projects

| Project | Purpose |
|---|---|
| `AIPolicyEngine.Api` | Core service — all endpoints, services, repositories |
| `AIPolicyEngine.AppHost` | .NET Aspire orchestrator for local dev |
| `AIPolicyEngine.ServiceDefaults` | Shared OpenTelemetry, health checks, resilience config |
| `AIPolicyEngine.Tests` | xUnit test suite (27 files, 198+ tests) |
| `AIPolicyEngine.Benchmarks` | BenchmarkDotNet perf tests |
| `AIPolicyEngine.LoadTest` | NBomber load tests |
| `aipolicyengine-ui` | React 19 / Vite / Tailwind 4 / shadcn/ui dashboard |

### Storage

- **Cosmos DB** — source of truth for all configuration (`plans`, `clients`, `routing-policies`, `pricing`, `access-profiles`, `audit-logs`, `billing-summaries`)
- **Azure Managed Redis** — write-through cache on top of Cosmos (`plan:{id}`, `client:{appId}:{tenantId}`), sliding-window rate limit counters (2-min TTL), distributed locks for quota updates (30s TTL)

### Core Services (`src/AIPolicyEngine.Api/Services/`)

- **`RoutingEvaluator`** — pure stateless logic (no I/O); evaluates `ModelRoutingPolicy` rules in priority order. Fully unit-tested.
- **`ChargebackCalculator`** — cost calculation with in-memory pricing cache (30s refresh). Supports token-based and multiplier-based billing.
- **`CachedRepository<T>`** — generic Cosmos + Redis write-through wrapper used by all 7 entity repositories.
- **`AuditLogWriter`** — background service using an unbuffered channel to batch `AuditLogItem` records to Cosmos.
- **`ApimPolicyApplyService`** — background service that renders policy templates and applies them to APIM via ARM API.
- **`PurviewAuditService`** — optional DLP check + audit emission (Microsoft.Agents.AI.Purview).

### Billing Models

Two modes, both supported simultaneously per plan:

- **Token-based**: `PlanData.MonthlyTokenQuota` + `CostPerMillionTokens` + `AllowOverbilling`
- **Multiplier-based (GHCP-style)**: `PlanData.UseMultiplierBilling` + `MonthlyRequestQuota` + per-deployment multipliers in `ModelPricing`

The dashboard adapts its UI based on which billing mode a plan uses.

### Authentication

Configured via `AuthProvider` setting at runtime. Two modes:

- **Entra ID** (default): Two-app model — Gateway App (multi-tenant, audience `api://{gateway-app-id}`) for clients; API App (single-tenant) for backend. Claims used: `tid`, `appid`/`azp`.
- **Keycloak**: OIDC discovery + JWT validation; roles from `realm_access.roles` and `resource_access.<client>.roles`.

Authorization policies in `Program.cs`:
- `AdminPolicy` — role `AIPolicy.Admin` (plan/client/pricing CRUD)
- `ApimPolicy` — role `AIPolicy.Apim` (precheck and log ingest endpoints)
- `ExportPolicy` — role `AIPolicy.Export` (CSV export endpoints)

No APIM subscription keys — all auth is bearer token only.

### Multi-Tenant Model

Customer identity is `{clientAppId}:{tenantId}`. This key is used for Cosmos partitioning, Redis keys, quota tracking, and billing isolation. A single `ClientPlanAssignment` document tracks current plan + accumulated usage for the billing period.

### Frontend Auth (`src/aipolicyengine-ui/src/auth/`)

The SPA detects auth provider at startup via `/api/auth-config`. Two implementations:
- `msalAuth.ts` — MSAL for Entra ID
- `keycloakAuth.ts` — oidc-client-ts for Keycloak

`authProvider.ts` selects the correct implementation at runtime.

## Testing Patterns

- **Unit tests** for pure logic (routing, cost calculation) use no external dependencies
- **Integration tests** use `FakeRedis.cs` (in-memory mock) and `Microsoft.AspNetCore.Mvc.Testing` for endpoint tests
- **Repository tests** validate Cosmos + cache interaction with mocked Cosmos client
- Test data lives alongside test classes; no shared fixtures required for unit tests

## Central Package Versioning

All NuGet package versions are managed in `src/Directory.Packages.props`. Do not specify versions in individual `.csproj` files.
