# APIM Policy Management Architecture

**Author:** McNulty (Lead / Architect)  
**Date:** 2026-05-16  
**Status:** Proposal — awaiting Zack approval  
**Requested by:** Zack Way  

---

## TL;DR — Consequential Decisions

1. **Tier B (template apply).** Users pick templates, fill params, engine generates + applies XML. No raw XML editor (too risky for v1).
2. **Non-AI work reshapes:** Sydnor's `entra-jwt-rest-policy.xml` ships as-is but becomes the **seed template**. Convert `{{NonAiRequestsPerMinute}}` etc. to template parameters. Precheck-rest endpoint stays (dashboard visibility > APIM-native counters), but limits are now *assignable from the APIM management UI*, not just the Plans page.
3. **SDK choice:** `Azure.ResourceManager.ApiManagement` via managed identity. No Terraform in the runtime loop.
4. **Storage:** New document type in existing `configuration` container. No new Cosmos container.

---

## 1. Scope — Tier B (Template Apply)

**Recommendation: Tier B.** Rationale:
- Tier A (read-only) doesn't solve the problem ("without editing XML files").
- Tier C (raw XML editor) is a foot-gun — invalid XML instantly breaks APIs. Defer to M4+.
- Tier D (drift detection) is a nice-to-have layered on later, not a launch requirement.

Tier B means: the engine ships a **template library**. The UI lets admins select a template, fill parameter values (RPM, quota, audiences, backend URL), and click Apply. The engine renders final XML and pushes it to APIM via SDK. Existing `.xml` files in `policies/` become seed templates.

---

## 2. Identity & Permissions

### Managed Identity → APIM

The Container App's system-assigned managed identity needs a **custom role** scoped to the APIM instance:

```
Actions:
  Microsoft.ApiManagement/service/apis/read
  Microsoft.ApiManagement/service/apis/operations/read
  Microsoft.ApiManagement/service/apis/policies/read
  Microsoft.ApiManagement/service/apis/policies/write
  Microsoft.ApiManagement/service/apis/policies/delete
  Microsoft.ApiManagement/service/apis/operations/policies/read
  Microsoft.ApiManagement/service/apis/operations/policies/write
  Microsoft.ApiManagement/service/apis/operations/policies/delete
```

**Not** `API Management Service Contributor` (too broad — includes delete APIs, manage subscriptions, etc.).

Terraform defines this custom role in `infra/terraform/modules/gateway/` and assigns it to the Container App identity.

### End-User Authorization

Reuse existing `AIPolicy.Admin` app role. Policy management is an admin-only action. No new role needed for v1.

### Multi-Tenant

The APIM instance is **single-tenant from the engine's perspective** — one engine manages one APIM. If the customer has multiple APIM instances, they deploy multiple engine instances. No cross-tenant API visibility concern.

---

## 3. APIM SDK vs ARM REST vs Terraform

**Decision: `Azure.ResourceManager.ApiManagement` NuGet package.**

Rationale:
- Idiomatic .NET, `DefaultAzureCredential` works with managed identity out of the box.
- Strongly typed — `ApiManagementApiResource`, `PolicyContractData`, etc.
- Terraform is declarative/offline — fundamentally wrong for "user clicks Apply in a UI."
- Raw ARM REST means hand-rolling auth token management and error parsing.

Package: `Azure.ResourceManager.ApiManagement` (GA, stable, current version ~1.3.0). No preview risk.

Configuration: store APIM resource ID as app setting `APIM_RESOURCE_ID` (format: `/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ApiManagement/service/{name}`).

---

## 4. Storage Model

### Location

Existing `configuration` container in Cosmos. New document type with partition key = `"policy-assignment"`.

### Document Shape

```json
{
  "id": "pa:{apiId}:{operationId|_all}",
  "partitionKey": "policy-assignment",
  "apiId": "azure-openai-jwt-based-api",
  "operationId": null,
  "apiDisplayName": "Azure OpenAI Service API",
  "templateId": "entra-jwt-ai",
  "templateVersion": "1.0",
  "parameters": {
    "ExpectedAudience": "api://abc123",
    "ContainerAppUrl": "https://myapp.azurecontainerapps.io",
    "ContainerAppAudience": "api://def456"
  },
  "generatedXmlHash": "sha256:abcdef...",
  "lastAppliedAt": "2026-05-16T10:00:00Z",
  "appliedBy": "user@contoso.com",
  "status": "synced",
  "createdAt": "2026-05-16T09:55:00Z",
  "updatedAt": "2026-05-16T10:00:00Z"
}
```

### Status Values

- `synced` — XML in APIM matches what we generated.
- `pending` — assignment saved but not yet applied (apply failed or deferred).
- `drifted` — detected that APIM XML differs from our generated hash (future M4).

### Drift Handling (deferred to M4)

On next apply or manual "sync check," compare APIM's current policy XML hash against `generatedXmlHash`. If different → mark `drifted`. Apply always **overwrites** — the engine is authoritative once it owns an API's policy.

---

## 5. Policy Template Library

### Ship-in-the-box Templates

| ID | Description | Source |
|---|---|---|
| `entra-jwt-ai` | JWT auth + precheck + routing + AI logging | `entra-jwt-policy.xml` |
| `entra-jwt-ai-dlp` | Above + DLP content-check | `entra-jwt-policy-dlp.xml` |
| `subscription-key-ai` | Sub-key auth + precheck + routing + AI logging | `subscription-key-policy.xml` |
| `subscription-key-ai-dlp` | Above + DLP | `subscription-key-policy-dlp.xml` |
| `entra-jwt-rest` | JWT auth + rate-limit + quota + REST logging | `entra-jwt-rest-policy.xml` |

### Template Format

**`{{placeholder}}` substitution.** Simple, proven (APIM named values already use this syntax). No DSL, no T4.

Each template is an XML file with `{{ParamName}}` tokens plus a companion `template.json` manifest:

```json
{
  "id": "entra-jwt-rest",
  "displayName": "Entra JWT — Non-AI REST (Rate Limit + Quota)",
  "version": "1.0",
  "parameters": [
    { "name": "ExpectedAudience", "type": "string", "required": true, "description": "Token audience" },
    { "name": "NonAiRequestsPerMinute", "type": "int", "required": true, "default": 60 },
    { "name": "NonAiMonthlyRequestQuota", "type": "int", "required": true, "default": 10000 },
    { "name": "NonAiBackendUrl", "type": "string", "required": true },
    { "name": "ContainerAppUrl", "type": "string", "required": true },
    { "name": "ContainerAppAudience", "type": "string", "required": true }
  ],
  "scope": "api"
}
```

### Repo Location

`policies/templates/` — each template gets a folder:
```
policies/templates/
  entra-jwt-ai/
    policy.xml
    template.json
  entra-jwt-rest/
    policy.xml
    template.json
  ...
```

---

## 6. Intersection with Non-AI API Limits (CRITICAL)

### What changes:

| Aspect | Before (non-AI spec) | After (this architecture) |
|---|---|---|
| XML file | Static `policies/entra-jwt-rest-policy.xml` | Becomes seed for `entra-jwt-rest` template |
| Limit values | APIM named values set at deploy time | Template parameters, configurable per-API from UI |
| `precheck-rest` endpoint | Exists as alternative (commented) | Still exists — option B for customers who need dashboard-visible real-time counters |
| User config surface | Plans page (flat fields) | Plans page sets *plan defaults*; APIM management page applies *per-API* |

### Directive for Sydnor:

**Ship `entra-jwt-rest-policy.xml` as-is.** It's done and correct. Immediately after merge, it gets copied into `policies/templates/entra-jwt-rest/policy.xml` with `{{placeholder}}` tokens preserved (they're already there). No wasted work.

### Cohesive model:

- **Plans page** → defines plan-level default limits (NonAiRequestsPerMinute, NonAiMonthlyRequestQuota). These are the *defaults* used when a template is applied without overrides.
- **APIM Management page** → assigns templates to APIs/operations. Parameter values can override plan defaults or use them.
- **Non-AI limits flow:** Plan defines limits → admin assigns `entra-jwt-rest` template to a non-AI API → engine renders XML with plan limits as default param values → applies to APIM.

### Precheck-rest stays:

APIM-native `rate-limit-by-key` is the **default** in the template (simple, no extra hop). But the template XML keeps the commented precheck-rest block. If a customer needs dashboard-level real-time enforcement (not just post-hoc log accounting), they toggle a template parameter `EnforcementMode: native|engine` that uncomments the precheck-rest block and comments out native limits. This is a v2 enhancement — for v1, native APIM enforcement is the default.

---

## 7. Backend API Surface

All endpoints require `AIPolicy.Admin` role.

```
GET    /api/apim/apis
       → 200: { apis: [{ id, displayName, path, serviceUrl, isCurrent }] }

GET    /api/apim/apis/{apiId}/operations
       → 200: { operations: [{ id, displayName, method, urlTemplate }] }

GET    /api/apim/apis/{apiId}/policy
       → 200: { assignment: PolicyAssignment | null, currentXml: string }

GET    /api/apim/apis/{apiId}/operations/{operationId}/policy
       → 200: { assignment: PolicyAssignment | null, currentXml: string }

GET    /api/apim/templates
       → 200: { templates: [{ id, displayName, version, parameters, scope }] }

POST   /api/apim/apis/{apiId}/policy
       Body: { templateId, parameters: { key: value } }
       → 202: { assignmentId, status: "applying" }
       (async — APIM apply can take 5-30s)

POST   /api/apim/apis/{apiId}/operations/{operationId}/policy
       Body: { templateId, parameters: { key: value } }
       → 202: { assignmentId, status: "applying" }

DELETE /api/apim/apis/{apiId}/policy
       → 200: { status: "cleared" }
       Behavior: removes engine assignment, sets APIM policy to <policies><inbound><base/></inbound>...</policies> (passthrough)
```

**Apply is async (202).** APIM is slow. The UI polls `GET .../policy` and checks `assignment.status` for `synced` or `failed`.

---

## 8. Frontend Shape

**New top-level page: "APIs"** (between "Routing Policies" and "Export" in the nav).

Layout:
- Left panel: tree view — list of APIs from APIM. Click an API to expand its operations.
- Right panel: selected item's policy details.
  - Shows: current template assignment (if any), parameter values, last-applied timestamp, status badge (synced/pending/failed).
  - Action: "Assign Template" button → opens a form: pick template from dropdown, fill parameter fields (with plan defaults pre-populated where applicable), click Apply.
  - Action: "Clear Assignment" → reverts to passthrough.

Fits the existing pattern: `Plans.tsx`, `RoutingPolicies.tsx`, `Pricing.tsx` are all list+detail pages with forms.

---

## 9. Sequencing & Dependencies

| Milestone | Scope | Agent | Depends On |
|---|---|---|---|
| **M1** | Read-only catalog: `GET /api/apim/apis`, `GET .../operations`, custom role + identity in Terraform | Sydnor (infra) + Freamon (endpoints) | Sydnor finishes non-AI XML |
| **M2** | Template library in repo, `GET /api/apim/templates`, template rendering service | Freamon | M1 |
| **M3** | Apply flow: `POST .../policy`, Cosmos storage, async apply via SDK | Freamon | M1, M2 |
| **M4** | UI "APIs" page — tree view, assign template, status display | Kima | M1, M2, M3 |
| **M5** | Operation-level granularity (operation-scoped apply) | Freamon + Kima | M3, M4 |
| **M6** | Drift detection (background poll, status: drifted) | Freamon | M3 |

**Recommended next deliverable: M1–M4.** Gets the full loop working for API-level policies. Operation-level and drift are fast follow-ons.

---

## 10. Risks & Open Questions

| Risk | Mitigation |
|---|---|
| Bad XML breaks API immediately | Validate rendered XML against APIM schema before apply. On failure, store `status: failed` with error. Consider a "preview XML" step in UI. |
| APIM apply latency (5-30s) | Async 202 pattern. UI shows spinner + polls. |
| APIM revision targeting | Always target the `current` revision. If customer uses revisions, that's out of scope for v1. |
| Rollback story | Store previous `generatedXmlHash` + XML. "Revert" re-applies prior version. v1: manual revert via re-assigning prior template params. |
| Drift from portal edits | v1: no detection. v2 (M6): periodic hash comparison. |
| Cost of APIM SDK calls | List APIs is cheap (cached client-side 60s). Apply is infrequent. No polling cost until M6. |

**Open question for Zack:** Do we need multi-APIM support (one engine managing N APIM instances), or is 1:1 sufficient? Recommendation: 1:1 for v1, config array for v2.

---

## 11. Test Scope

| Layer | Strategy |
|---|---|
| Template rendering | Unit tests: given template XML + params → assert rendered XML. No Azure dependency. Pure string substitution. |
| APIM SDK integration | **Recorded HTTP fixtures** via `Azure.Core.TestFramework` (same pattern as other Azure SDK tests). Record once against real APIM, replay in CI. |
| Apply flow (end-to-end) | Integration test with a mock `IApimPolicyService` interface. Verify Cosmos document created, status transitions. |
| UI | Component tests for the APIs page (React Testing Library). Mock API responses. |
| Destructive/live | Manual smoke test in Zack's test environment. Not automated in CI — too expensive and slow. |

Bunk writes unit tests for template rendering and the apply orchestrator. Integration tests use recorded fixtures — no live APIM in CI.

---

## Appendix: APIM Resource ID Configuration

Add to Container App environment:
```
APIM_RESOURCE_ID=/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ApiManagement/service/{name}
```

Terraform outputs this from the gateway module. Postprovision wires it into the Container App config.
