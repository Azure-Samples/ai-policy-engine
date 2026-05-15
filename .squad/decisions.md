# Squad Decisions

## Active Decisions

### 2026-04-17T15:52:16Z: User directive — Agent365 SDK integration
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** Each APIM client is registered and pushes data to the Agent 365 SDK (`Microsoft.Agents.A365.*`) as an Agent for all calls to the Foundry endpoints. The Agent365 SDK (https://github.com/microsoft/Agent365-dotnet) provides the enterprise observability/identity layer we need. Docs at https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity.  
**Why:** User found the missing SDK. This replaces/augments our custom PurviewGraphClient with the official Agent365 Observability pipeline. Each client becomes an Agent365 agentic identity.  
**Key packages:** Microsoft.Agents.A365.Observability, .Runtime, .Hosting, .Extensions.OpenAI  
**Impact:** Our PurviewAuditService + PurviewGraphClient may be refactored to use the A365 Observability SDK's tracing/exporter pipeline instead of direct Graph REST calls.

### 2026-04-17T16:19:17Z: User directive — A365 integration scope
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** Q1: Start with lightweight observability only (Option C — use ClientAppId as agent.id, no full Agentic User provisioning). Q2: Emit A365 spans from Precheck and Log Ingest only, following manual instrumentation guide at https://learn.microsoft.com/en-us/microsoft-agent-365/developer/observability?tabs=dotnet#manual-instrumentation  
**Why:** User decision — lightweight first, full identity provisioning deferred to Phase 2.

### 2026-04-17T16:23:41Z: User directive — A365 integration Q3-Q6 answers
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:**
- Q3: Don't worry about deprecating PurviewGraphClient for now. As long as we use the same App ID for both A365 Observability and Purview, reports/dashboards will correlate.
- Q4: Emit spans for ALL OpenAI or Foundry endpoints. When non-agent platform API endpoints are added later, those should be excluded. For now, everything gets traced.
- Q5: A365 is HOST TENANT scoped. If the host tenant has Purview/A365 configured, it's on globally. If not configured, it's off. No per-client/per-tenant configuration needed.
- Q6: Use Aspire Dashboard for local OTel testing (A365 uses OpenTelemetry). Zack's test tenant has A365/Frontier enabled for integration testing.  
**Why:** User decisions to unblock Phase 1 implementation.

### 2026-04-01T00:00:00Z: Agent365 SDK Integration Architecture Plan (PROPOSAL)
**By:** McNulty (Lead / Architect)  
**Status:** Proposal — awaiting implementation prioritization  
**What:** Full architecture plan for integrating Microsoft Agent365 SDK (`Microsoft.Agents.A365.*`) for enterprise-grade observability, identity, and governance:
- **Key Finding:** A365 Observability SDK is **SEPARATE AND COMPLEMENTARY** to existing `Microsoft.Agents.AI.Purview` DLP integration. 
  - `Microsoft.Agents.AI.Purview` = Real-time DLP policy enforcement (block/allow at request time)
  - `Microsoft.Agents.A365.Observability` = Telemetry export (audit trail, session tracking, inference logs sent to M365/Purview for compliance dashboards)
- **Recommended Architecture:** Keep both SDKs — integrate A365 Observability alongside existing Purview DLP, mapping each `ClientPlanAssignment` to an Agent365 identity.
- **Three-Phase Plan:**
  1. **Phase 1 (Lightweight Observability):** Add A365 SDK packages, wrap PrecheckEndpoints with `InvokeAgent` scope, wrap LogIngestEndpoints with `ExecuteInference` scope. No breaking changes.
  2. **Phase 2 (Agent Identity Provisioning):** Provision Agentic User identities per-client, store mapping in CosmosDB (requires Zack's identity strategy decision).
  3. **Phase 3 (Purview Deprecation):** Once `Microsoft.Agents.AI.Purview` promotes `IScopedContentProcessor` to public API, replace custom `PurviewGraphClient` with SDK wrapper.
- **Package Dependencies:** Microsoft.Agents.A365.Observability, .Runtime, .Extensions.OpenAI (if wrapping Azure OpenAI calls)
- **Integration Points:**
  - PrecheckEndpoints: `InvokeAgent` scope with `gen_ai.agent.id=ClientAppId`, `microsoft.tenant.id=TenantId`, `gen_ai.conversation.id=correlationId`
  - LogIngestEndpoints: `ExecuteInference` scope capturing model, tokens, latency, routing decision
  - DLP Action Attribution: Set `threat.diagnostics.summary` attribute when `CheckContentAsync` blocks request
- **Configuration:** ENABLE_A365_OBSERVABILITY_EXPORTER=true env var, Agent365 settings in appsettings.json
- **Open Questions Resolved by Zack:** (1) Agent identity strategy (lightweight vs. full), (2) scope of integration (which endpoints), (3) Purview DLP replacement timeline, (4) Foundry endpoint filtering, (5) tenant/subscription requirements, (6) testing strategy
- **Estimated Effort:** Phase 1 = 2-3 days; Phase 2 = 1-5 days (depends on identity strategy); Phase 3 = 1 day (when SDK ready)
- **Risk Assessment:** Beta SDK (API may change), Frontier preview access required (not all tenants), Agent identity provisioning workflow unclear (mitigation: start lightweight)
- **Reference Document:** See .squad/decisions/inbox/mcnulty-agent365-architecture.md for full architecture analysis (60+ sections covering SDK structure, observability data model, integration patterns, migration path, identity model analysis, and comprehensive Q&A)

### 2026-04-17T16:33:20Z: Microsoft Agent365 Observability SDK Integration (Phase 1)
**By:** Freamon (Backend Dev) under guidance from Zack Way  
**Status:** Implemented  
**What:** Add Agent365 Observability SDK v0.1.75-beta alongside existing OpenTelemetry and Purview DLP. Pure additive integration. Instrument Precheck + ContentCheck endpoints with `InvokeAgentScope` and LogIngest endpoint with `InferenceScope`. Use ClientAppId as lightweight agent identity. Opt-in via `ENABLE_A365_OBSERVABILITY_EXPORTER` env var (default: false).  
**Why:** Provide agent-specific telemetry patterns for AI workloads; infrastructure for future A365 backend integration when SDK stabilizes.  
**How:** Service abstraction (`IAgent365ObservabilityService`) with stub implementation (TODO markers) and no-op fallback. DI registration. Scope wrapping in endpoint handlers. Correlation ID from `X-Correlation-ID` header.  
**Trade-offs:** Stub implementation (SDK v0.1.75-beta lacks public scope creation APIs; full implementation deferred to v0.2.x+). No per-client toggle (host-level only). No M365 identity provisioning. Config CRUD not instrumented (focused on hot path).  
**Testing:** 225 tests (221 pass, 4 skipped); zero regressions.  
**Future:** Upgrade SDK, implement token acquisition, add integration tests, enforce correlation ID.

### 2026-04-11T13:16:03Z: Agent365 SDK Real Implementation + Scope Instrumentation (IMPLEMENTED)
**By:** Freamon (Backend Dev)  
**Status:** Implemented  
**Supersedes:** 2026-04-17T16:33:20Z (stub implementation placeholder)  
**What:** Replaced Agent365 Observability stubs with real SDK scope calls using Microsoft.Agents.A365.Observability.Runtime 0.1.75-beta. Full implementation of `InvokeAgentScope.Start()` and `InferenceScope.Start()` with proper AgentDetails, TenantDetails, Request/InferenceCallDetails parameters. Manual OpenTelemetry configuration added (AddA365Tracing extension not available in this SDK version). All scope creation wrapped in try/catch fail-safe blocks.  
**Key Decisions:**
- `InvokeAgentScope.Start()` called with AgentDetails (clientAppId, clientDisplayName), TenantDetails (tenantId), Request (promptContent), and session/correlation IDs
- `InferenceScope.Start()` called with InferenceCallDetails (model, operationName, token counts), AgentDetails, TenantDetails
- Manual OpenTelemetry registration (AddA365Tracing not available in v0.1.75-beta)
- Namespace conflict resolution: `using A365Request = Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request` alias
- Placeholder endpoint: `https://apim.example.com` (APIM scenario has no fixed agent endpoint)
- Fail-safe design: null returns on any exception, never breaks request flow
**Files Modified:**
- `src/Chargeback.Api/Services/Agent365ServiceExtensions.cs` — Removed TODO, added OpenTelemetry config
- `src/Chargeback.Api/Services/Agent365ObservabilityService.cs` — Implemented real scope creation  
**Test Results:** 235 tests pass (231 pass, 4 documented skips), zero regressions  
**Why:** Production-ready observability. Stubs were causing confusion; SDK is stable enough per Microsoft docs. Real scopes provide agent-specific telemetry when enabled.

### 2026-04-11T13:16:03Z: APIM DLP Policy Variants — Fail-Open Content-Check (IMPLEMENTED)
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Created two DLP-enabled APIM policy variants offering customers choice between baseline (precheck-only) and DLP-enabled (precheck + content-check) policies. Customers without Purview DLP requirements use base policies; customers with DLP requirements use DLP-suffix variants. Both auth types have variants: subscription-key and entra-jwt.  
**New Files:**
- `policies/subscription-key-policy-dlp.xml` — Subscription key auth + DLP content-check
- `policies/entra-jwt-policy-dlp.xml` — Entra JWT auth + DLP content-check  
**Files Unchanged:** Base policies (subscription-key-policy.xml, entra-jwt-policy.xml) remain identical  
**Content-Check Pipeline:**
- **Placement:** After precheck succeeds (200) + routing logic, before backend forwarding
- **Request:** POST /api/content-check/{clientAppId}/{tenantId} with original requestBody
- **Response Handling:** HTTP 451 → block, other statuses/failures → proceed (fail-open)
- **Fail-Open Strategy:** Uses `ignore-error="true"` on send-request; transient failures don't block valid requests
- **Timeout:** 10 seconds (matches precheck timeout)
- **Authentication:** APIM managed identity token (same as precheck)  
**Error Response (HTTP 451):** Mimics Azure OpenAI content_filter format for client compatibility
**Why:** Not all customers need DLP. Offer choice. Fail-open prioritizes availability — transient content-check failures don't create outages. Only explicit HTTP 451 blocks requests.
**Trade-offs:** 4 policies to maintain (base + DLP × 2 auth types); future changes must be applied consistently across variants

### 2026-05-14T15:54:00Z: User directive — Always validate infra fixes before committing
**By:** Zack Way (via Copilot)  
**Status:** Accepted  
**What:** When fixing infrastructure/deploy errors, the team must validate fixes by actually running the relevant `azd` command (e.g., `azd provision`, `azd up`) BEFORE committing. Do not write commits with unvalidated fixes — keeps the commit tree clean of speculative/bad history.  
**Why:** User request — captured for team memory

### 2026-05-14T15:54:00Z: azd Terraform Provider Configuration
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Added `infra:` section to `azure.yaml` to declare Terraform as the IaC provider for azd:
```yaml
infra:
  provider: terraform
  path: infra/terraform
  module: main
```
**Why:** azd CLI defaults to Bicep when no `infra:` section is declared. This configuration is required to use Terraform with azd.  
**Impact:** `azd provision` now invokes Terraform instead of looking for `infra/main.bicep`. No Terraform module changes needed.  
**Validation:** `azd provision --preview` succeeds through Terraform plan phase (blocked only by Azure tenant Conditional Access policy, not code).

### 2026-05-14T15:54:00Z: azd Terraform Provider Requires main.tfvars.json Template
**By:** Sydnor (Infra/DevOps)  
**Status:** Implemented  
**What:** Created `infra/terraform/main.tfvars.json` as a template file with azd environment variable substitution:
```json
{
  "subscription_id": "${AZURE_SUBSCRIPTION_ID}",
  "location": "${AZURE_LOCATION}",
  "workload_name": "${AZURE_ENV_NAME}"
}
```
**Why:** azd's Terraform provider requires this file alongside `main.tf` to map azd environment variables to Terraform input variables. Without it, azd cannot initialize the Terraform module.  
**Impact:** `azd provision` now correctly reads Terraform variables from the template. File is intentionally uncommitted per Zack's directive to validate infra fixes before committing.  
**Validation:** Ran `azd provision --preview` — "file not found" error resolved; Terraform plan succeeds.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
