# AAA Per-Client Architecture — Pre/Post Endpoint Contracts Addendum

**Author:** McNulty (Lead / Architect)  
**Date:** 2026-05-21  
**Status:** Addendum to approved architecture  
**Parent:** `.squad/decisions/inbox/mcnulty-aaa-per-client-arch.md`  

---

## 1. Precheck Endpoint — Request Contract

### Current Signature

```
GET /api/precheck/{clientAppId}/{tenantId}?deploymentId={deploymentId}
```

Route params: `clientAppId`, `tenantId`  
Query params: `deploymentId`

### New Signature (backward-compatible additions)

```
GET /api/precheck/{clientAppId}/{tenantId}?deploymentId={deploymentId}&apiId={apiId}&operationId={operationId}&subscriptionId={subscriptionId}
```

New query parameters:

| Param | Source in APIM XML | Required | Fallback if absent |
|-------|-------------------|----------|-------------------|
| `apiId` | `context.Api.Id` | No | Resolver skips to level 4 (legacy `ClientPlanAssignment`) |
| `operationId` | `context.Operation.Id` | No | Resolver treats as `_all` (API-level match) |
| `subscriptionId` | `context.Subscription.Id` | No | Not used for resolution — informational for audit/correlation only |

### Backward Compatibility

The endpoint handler checks for `apiId` presence:

```csharp
var apiId = context.Request.Query["apiId"].ToString();
var operationId = context.Request.Query["operationId"].ToString();

if (!string.IsNullOrEmpty(apiId))
{
    // NEW PATH: Access Profile resolution
    var resolved = await accessProfileResolver.ResolveAsync(
        clientAppId, tenantId, apiId, 
        string.IsNullOrEmpty(operationId) ? null : operationId, ct);
    
    if (resolved is not null)
    {
        // Use resolved.PlanId, resolved.RoutingPolicyId, resolved.AllowedDeployments
        plan = await planRepo.GetAsync(resolved.PlanId);
        // ... continue with resolved plan
    }
    else
    {
        // No profile matched → fall through to ClientPlanAssignment (existing code)
    }
}
else
{
    // LEGACY PATH: no apiId means old template, use ClientPlanAssignment directly (unchanged)
}
```

**Zero breaking change.** Existing templates without `apiId`/`operationId` params hit the exact same code path as today.

### Why `subscriptionId` (not `subscriptionName`)

For sub-key flows, `clientAppId` is already set to `context.Subscription.Name` in the template XML. The `subscriptionId` is additive metadata — useful for:
- Audit: correlate requests to the APIM subscription resource
- Future: lifecycle events (subscription revoked → log it)

It does NOT participate in resolution. Resolution key is always `clientAppId:tenantId`.

---

## 2. Precheck Endpoint — Response Contract

### Current Response (200 OK)

```json
{
  "status": "authorized",
  "clientAppId": "my-app",
  "tenantId": "contoso-tid",
  "plan": "Enterprise Plan",
  "usage": 45000,
  "limit": 100000,
  "currentRpm": 5,
  "rpmLimit": 60,
  "currentTpm": 1200,
  "tpmLimit": 10000,
  "routedDeployment": "gpt-4o-eastus",
  "requestedDeployment": "gpt-4o",
  "routingPolicyId": "cost-optimized"
}
```

### New Response (200 OK) — additive fields

```json
{
  "status": "authorized",
  "clientAppId": "my-app",
  "tenantId": "contoso-tid",
  "plan": "Enterprise Plan",
  "planId": "enterprise-plan",
  "usage": 45000,
  "limit": 100000,
  "currentRpm": 5,
  "rpmLimit": 60,
  "currentTpm": 1200,
  "tpmLimit": 10000,
  "routedDeployment": "gpt-4o-eastus",
  "requestedDeployment": "gpt-4o",
  "routingPolicyId": "cost-optimized",
  "accessProfileId": "ap:my-app:contoso-tid:openai-api:_all",
  "allowedDeployments": ["gpt-4o", "gpt-4o-mini"]
}
```

**New fields:**

| Field | Type | When present | Purpose |
|-------|------|-------------|---------|
| `planId` | string | Always (new) | Machine-readable plan identifier. Today we only return `plan` (display name). Templates need the ID for the log payload. |
| `accessProfileId` | string? | When resolved via Access Profile (levels 1-3) | Identifies which profile authorized this request. Null when falling through to legacy `ClientPlanAssignment`. |
| `allowedDeployments` | string[]? | When profile or plan has restrictions | Effective deployment allowlist after resolution. Null = unrestricted. |

**Deny responses (401/403/429) — no change.** The existing error shapes are sufficient. Adding one optional field to denials:

```json
{
  "error": "Client not authorized — no plan assigned",
  "clientAppId": "my-app",
  "tenantId": "contoso-tid",
  "accessProfileId": null,
  "deniedBy": "no-profile-no-assignment"
}
```

`deniedBy` values: `"no-profile-no-assignment"`, `"profile-disabled"`, `"quota-exceeded"`, `"rate-limit"`, `"deployment-denied"`, `"routing-denied"`. Purely informational for debugging.

---

## 3. Log Endpoint — Request Contract

### Current `LogIngestRequest` Fields

```csharp
TenantId, ClientAppId, Audience, DeploymentId, 
RequestBody, ResponseBody, RoutingPolicyId, CorrelationId
```

### New Fields (additive)

```csharp
/// <summary>Access Profile ID that authorized this request (from precheck response).</summary>
public string? AccessProfileId { get; set; }

/// <summary>Plan ID resolved for this request (from precheck response).</summary>
public string? PlanId { get; set; }

/// <summary>APIM API ID for endpoint-scoped accounting.</summary>
public string? ApiId { get; set; }

/// <summary>APIM Operation ID for operation-scoped accounting.</summary>
public string? OperationId { get; set; }
```

### How Profile ID Flows: Precheck → APIM → Log

**Mechanism: APIM `context.Variables` slot.**

The template already parses the precheck response body to extract `routedDeployment` and `routingPolicyId`. We add extraction of `accessProfileId` and `planId` using the same pattern:

```xml
<!-- Extract access profile metadata from precheck response (null-safe) -->
<set-variable name="accessProfileId"
    value="@(((IResponse)context.Variables["precheckResponse"]).Body.As<JObject>(preserveContent: true)["accessProfileId"]?.ToString())" />
<set-variable name="resolvedPlanId"
    value="@(((IResponse)context.Variables["precheckResponse"]).Body.As<JObject>(preserveContent: true)["planId"]?.ToString())" />
```

Then in the outbound `send-one-way-request` log payload:

```xml
payload.Add(new Newtonsoft.Json.Linq.JProperty("accessProfileId", 
    context.Variables.GetValueOrDefault<string>("accessProfileId") ?? ""));
payload.Add(new Newtonsoft.Json.Linq.JProperty("planId", 
    context.Variables.GetValueOrDefault<string>("resolvedPlanId") ?? ""));
payload.Add(new Newtonsoft.Json.Linq.JProperty("apiId", context.Api.Id));
payload.Add(new Newtonsoft.Json.Linq.JProperty("operationId", context.Operation.Id));
```

**Key:** `apiId` and `operationId` in the log payload come directly from `context.Api.Id` / `context.Operation.Id` — NOT from the precheck response. The precheck response carries the resolved profile metadata; the APIM context carries the request topology.

### `AuditLogItem` Additions

```csharp
public string? AccessProfileId { get; set; }
public string? ResolvedPlanId { get; set; }
public string? ApiId { get; set; }
public string? OperationId { get; set; }
```

These flow into the Cosmos audit document for full traceability.

---

## 4. Resolver Placement — Decision

**Decision: Resolve at precheck time only. Log endpoint trusts the profile id passed from APIM.**

Justification:

| Approach | Pros | Cons |
|----------|------|------|
| **Resolve at precheck only** (recommended) | Single hot-path lookup. Log endpoint is fire-and-forget (no blocking Cosmos read). Keeps outbound latency at zero additional hops. | If profile id is missing/corrupted in log payload, we lose the association. |
| Resolve at both precheck and log | Resilient to data loss in transit. | Doubles Cosmos reads on the hot path. Log endpoint already has the client lock contention concern — adding a resolver read compounds latency. |

**Resilience strategy for missing profile id at log time:**

```csharp
// In LogIngestEndpoints.cs:
// If accessProfileId is absent (old template or data loss), 
// fall back to ClientPlanAssignment.PlanId for accounting (existing behavior).
var effectivePlanId = !string.IsNullOrEmpty(ingestRequest.PlanId) 
    ? ingestRequest.PlanId 
    : clientAssignment.PlanId;
```

The log endpoint uses `PlanId` from the request body to load the correct plan for cost calculation. If absent, it uses `ClientPlanAssignment.PlanId` (today's behavior). **No second resolver call needed.**

**The log endpoint does NOT need the full resolver service.** It only needs to read `ingestRequest.PlanId` (a string) and call `planRepo.GetAsync(planId)`. The resolver is injected ONLY into `PrecheckEndpoints`.

---

## 5. Migration / Coexistence — Decision

**Decision: Single `IAccessProfileResolver` call that internally falls through to `ClientPlanAssignment`.**

The resolver encapsulates the entire cascade. Precheck calls it once and gets either a `ResolvedAccess` or `null`. If `null`, precheck uses `ClientPlanAssignment` directly (the existing code path, untouched).

### Implementation Shape

```csharp
// In PrecheckEndpoints.cs — the new integration point:

var apiId = context.Request.Query["apiId"].ToString();
var operationId = context.Request.Query["operationId"].ToString();

// Resolution
string effectivePlanId;
string? effectiveRoutingPolicyId;
List<string>? effectiveAllowedDeployments;
string? accessProfileId = null;

if (!string.IsNullOrEmpty(apiId))
{
    var resolved = await resolver.ResolveAsync(clientAppId, tenantId, apiId, 
        string.IsNullOrEmpty(operationId) ? null : operationId);
    
    if (resolved is not null)
    {
        effectivePlanId = resolved.PlanId;
        effectiveRoutingPolicyId = resolved.RoutingPolicyId;
        effectiveAllowedDeployments = resolved.AllowedDeployments;
        accessProfileId = resolved.SourceProfileId;
    }
    else
    {
        // No profile → use legacy path
        effectivePlanId = assignment.PlanId;
        effectiveRoutingPolicyId = assignment.ModelRoutingPolicyOverride;
        effectiveAllowedDeployments = assignment.AllowedDeployments;
    }
}
else
{
    // Old template, no apiId → entirely legacy path (zero behavior change)
    effectivePlanId = assignment.PlanId;
    effectiveRoutingPolicyId = assignment.ModelRoutingPolicyOverride;
    effectiveAllowedDeployments = assignment.AllowedDeployments;
}

// Load plan using effectivePlanId (rest of precheck unchanged)
var plan = await planRepo.GetAsync(effectivePlanId);
```

### Why NOT merge the resolver into `ClientPlanAssignment` lookup:

- **Separation of concerns.** `ClientPlanAssignment` is a billing/usage tracking entity (it has `CurrentPeriodUsage`, `OverbilledTokens`, etc.). Access Profiles are authorization entities. They serve different purposes.
- **The `ClientPlanAssignment` MUST still exist** for usage tracking regardless of Access Profile. Even when a profile overrides the plan, the usage counters live on `ClientPlanAssignment`. The profile says "which plan," but the assignment tracks "how much used."
- **Clean dependency:** `IAccessProfileResolver` depends only on `IRepository<AccessProfile>` + Redis cache. It has no knowledge of usage, billing periods, or rate limits.

### Log Endpoint — No Resolver Change

The log endpoint continues to load `ClientPlanAssignment` by `{clientAppId}:{tenantId}` (for usage counters). It loads the plan using `ingestRequest.PlanId` (from precheck→template→log) or falls back to `clientAssignment.PlanId`:

```csharp
// Log endpoint plan resolution (line ~112-118 today):
var planId = !string.IsNullOrEmpty(ingestRequest.PlanId) 
    ? ingestRequest.PlanId 
    : clientAssignment.PlanId;
var plan = await planRepo.GetAsync(planId);
```

This is the **only change** to `LogIngestEndpoints.cs` — a two-line modification. The rest of the accounting logic (quota tracking, overbilling, multiplier billing) uses the resolved `plan` variable exactly as today.

---

## 6. Template Updates — Exact Diffs

All 5 templates need the same mechanical changes. Here's the diff for `entra-jwt-ai/policy.xml` (representative — others are identical in the affected sections):

### Inbound: Add `apiId`/`operationId` variables (after existing claim extraction)

```xml
<!-- ADD after deploymentId extraction, before authentication-managed-identity -->
<set-variable name="apiId" value="@(context.Api.Id)" />
<set-variable name="operationId" value="@(context.Operation.Id)" />
```

### Inbound: Append to precheck URL

```xml
<!-- BEFORE -->
<set-url>@((string)context.Variables["containerAppBaseUrl"] + "/api/precheck/" + (string)context.Variables["clientAppId"] + "/" + (string)context.Variables["tenantId"] + "?deploymentId=" + (string)context.Variables["deploymentId"])</set-url>

<!-- AFTER -->
<set-url>@((string)context.Variables["containerAppBaseUrl"] + "/api/precheck/" + (string)context.Variables["clientAppId"] + "/" + (string)context.Variables["tenantId"] + "?deploymentId=" + (string)context.Variables["deploymentId"] + "&apiId=" + (string)context.Variables["apiId"] + "&operationId=" + (string)context.Variables["operationId"])</set-url>
```

### Inbound: Extract profile metadata from precheck response (after existing `routedDeployment` extraction)

```xml
<!-- ADD after routingPolicyId extraction -->
<set-variable name="accessProfileId"
    value="@(((IResponse)context.Variables["precheckResponse"]).Body.As<JObject>(preserveContent: true)["accessProfileId"]?.ToString())" />
<set-variable name="resolvedPlanId"
    value="@(((IResponse)context.Variables["precheckResponse"]).Body.As<JObject>(preserveContent: true)["planId"]?.ToString())" />
```

### Outbound: Add fields to log payload (in the `send-one-way-request` body)

```xml
<!-- ADD to the payload JObject construction -->
payload.Add(new Newtonsoft.Json.Linq.JProperty("accessProfileId", context.Variables.GetValueOrDefault<string>("accessProfileId") ?? ""));
payload.Add(new Newtonsoft.Json.Linq.JProperty("planId", context.Variables.GetValueOrDefault<string>("resolvedPlanId") ?? ""));
payload.Add(new Newtonsoft.Json.Linq.JProperty("apiId", context.Variables.GetValueOrDefault<string>("apiId") ?? ""));
payload.Add(new Newtonsoft.Json.Linq.JProperty("operationId", context.Variables.GetValueOrDefault<string>("operationId") ?? ""));
```

### Template Version Bump

All 5 `template.json` files: `"version": "1.0"` → `"version": "1.1"`.

No new parameters needed — `apiId`/`operationId` come from APIM context (free), not from user-supplied config.

### Which Templates Need Updates

| Template | Precheck? | Log? | Update needed? |
|----------|-----------|------|----------------|
| `entra-jwt-ai` | ✅ | ✅ | **Yes** — all 4 diffs above |
| `entra-jwt-ai-dlp` | ✅ | ✅ | **Yes** — all 4 diffs above |
| `subscription-key-ai` | ✅ | ✅ | **Yes** — all 4 diffs above |
| `subscription-key-ai-dlp` | ✅ | ✅ | **Yes** — all 4 diffs above |
| `entra-jwt-rest` | ❌ (uses native APIM limits) | ✅ (has log-rest) | **Yes** — outbound log diff only + add `apiId`/`operationId` variables |

---

## 7. Updated Milestone Breakdown

| Milestone | Scope | Agent | Depends On | Delta from prior spec |
|-----------|-------|-------|------------|----------------------|
| **M1** | `AccessProfile` model + `CosmosAccessProfileRepository` + `IAccessProfileResolver` with cascade + Redis cache layer | Freamon | None | Unchanged |
| **M2** | CRUD endpoints (`/api/access-profiles/*`) + bulk assign | Freamon | M1 | Unchanged |
| **M3** | **Precheck integration:** Inject resolver, add `apiId`/`operationId` query param parsing, extend response with `planId`/`accessProfileId`/`allowedDeployments`, backward-compat guard | Freamon | M1 | **Expanded** — now includes response contract changes |
| **M4** | **Log integration:** Add `AccessProfileId`/`PlanId`/`ApiId`/`OperationId` to `LogIngestRequest` + `AuditLogItem`, use `ingestRequest.PlanId` for plan resolution with fallback | Freamon | M3 | **NEW milestone** — split from old M4 |
| **M5** | **Template updates:** All 5 templates get the 4-diff treatment (variables, precheck URL, profile extraction, log payload). Version bump to 1.1. | Freamon/Sydnor | M3, M4 (endpoint must accept new fields before templates send them) | **Expanded** — was "trivial", now mechanical but spans 5 files |
| **M6** | UI — `/access` page: client selector, API grid, per-operation drill-down, assign form | Kima | M2 | Unchanged (renumbered from M5) |
| **M7** | Redis caching optimization — if M1's cache layer needs tuning after load test | Freamon | M3 | Unchanged (renumbered from M6) |

### Critical Path

```
M1 → M2 (UI can start)
M1 → M3 → M4 → M5 (backend pipeline, strictly serial)
M2 → M6 (UI, parallel to M3-M5)
```

**Thin slice for validation:** M1 + M3 + M5 (one template). Gets the full request pipeline working end-to-end for one template. M2/M4/M6 are additive.

---

## 8. Test Surface

### Resolver Unit Tests (Bunk — M1)

| Test Case | Input | Expected |
|-----------|-------|----------|
| Operation-level match | `clientAppId=X, tenantId=Y, apiId=A, operationId=O` with profile at level 1 | Returns level 1 profile |
| API-level match (no op match) | Same client, different operationId, profile only at level 2 | Returns level 2 profile |
| Global client match | Client+API with no profiles, but `_global:_all` exists | Returns level 3 profile |
| No match → null | Client with no profiles at any level | Returns null |
| Disabled profile skipped | Level 1 exists but `enabled=false`, level 2 exists | Returns level 2 |
| First-match-wins (no merge) | Profiles at level 1 and 2 with different planIds | Returns level 1 only |

### Precheck Integration Tests (Bunk — M3)

| Test Case | Setup | Assert |
|-----------|-------|--------|
| Legacy path (no apiId) | Call precheck without `apiId` param | Uses `ClientPlanAssignment.PlanId`, response has no `accessProfileId` |
| Profile-resolved path | Call with `apiId`, Access Profile exists | Response contains `accessProfileId`, `planId` from profile |
| Profile fallback to legacy | Call with `apiId`, no profile matches | Uses `ClientPlanAssignment.PlanId`, `accessProfileId` is null |
| Profile with routing override | Profile has `routingPolicyId` | `routedDeployment` in response reflects profile's routing, not plan's |
| Profile with deployment restriction | Profile has `allowedDeployments: ["gpt-4o"]`, request asks for `gpt-35` | 403 with `deniedBy: "deployment-denied"` |
| Disabled profile cascade | Op-level profile disabled, API-level exists | Uses API-level profile |

### Log Integration Tests (Bunk — M4)

| Test Case | Setup | Assert |
|-----------|-------|--------|
| Log with profile id | `LogIngestRequest` includes `accessProfileId`, `planId` | Audit log item has both fields, plan loaded from `planId` |
| Log without profile id (legacy) | `LogIngestRequest` omits `accessProfileId`/`planId` | Falls back to `ClientPlanAssignment.PlanId` (today's behavior) |
| Log with mismatched planId | `ingestRequest.PlanId` references non-existent plan | Falls back to `clientAssignment.PlanId` |
| Audit item carries all fields | Full log with profile id, apiId, operationId | `AuditLogItem` persisted with all 4 new fields |

### Template Render Tests (Bunk — M5)

| Test Case | Assert |
|-----------|--------|
| Rendered XML contains `apiId` variable extraction | `<set-variable name="apiId"` present |
| Precheck URL includes `&apiId=` and `&operationId=` | URL pattern matches |
| Log payload includes `accessProfileId`, `planId`, `apiId`, `operationId` properties | JProperty assertions on rendered outbound body |
| Template version is 1.1 | `template.json` version field check |

### End-to-End Cascade Test (Integration — M5)

One full-stack test that exercises the complete flow:
1. Create client + plan + routing policy + Access Profile in test Cosmos
2. Call precheck with `apiId`/`operationId` → assert profile-resolved response
3. Call log endpoint with the response's `accessProfileId`/`planId` → assert audit record
4. Remove profile → call precheck again → assert legacy fallback

---

## Appendix: Summary of Model Changes

### New Properties on Existing Models

**`LogIngestRequest`** (4 new optional fields):
```csharp
public string? AccessProfileId { get; set; }
public string? PlanId { get; set; }
public string? ApiId { get; set; }
public string? OperationId { get; set; }
```

**`AuditLogItem`** (4 new optional fields):
```csharp
public string? AccessProfileId { get; set; }
public string? ResolvedPlanId { get; set; }
public string? ApiId { get; set; }
public string? OperationId { get; set; }
```

### Precheck Response (anonymous object → consider a named type)

Recommend extracting the precheck 200 response into a named record for type safety:

```csharp
public sealed record PrecheckResponse(
    string Status,
    string ClientAppId,
    string TenantId,
    string Plan,
    string PlanId,
    long Usage,
    long Limit,
    long CurrentRpm,
    int RpmLimit,
    long CurrentTpm,
    int TpmLimit,
    string? RoutedDeployment,
    string? RequestedDeployment,
    string? RoutingPolicyId,
    string? AccessProfileId,
    List<string>? AllowedDeployments);
```

This is optional cleanup — the anonymous object works fine — but having a named type helps Bunk write assertions and Kima consume the API.

---

## Appendix: Request Flow Diagram (After All Milestones)

```
Client → APIM (XML template v1.1)
  │
  ├─ INBOUND:
  │   set-variable: apiId = context.Api.Id
  │   set-variable: operationId = context.Operation.Id
  │   send-request → GET /api/precheck/{clientAppId}/{tenantId}
  │                     ?deploymentId=X&apiId=Y&operationId=Z
  │   ← 200: { planId, accessProfileId, routedDeployment, ... }
  │   set-variable: accessProfileId = response.accessProfileId
  │   set-variable: resolvedPlanId = response.planId
  │   (routing rewrite if needed — unchanged)
  │
  ├─ BACKEND: → Azure OpenAI (or non-AI backend)
  │
  └─ OUTBOUND:
      send-one-way-request → POST /api/log
        { clientAppId, tenantId, deploymentId,
          accessProfileId, planId, apiId, operationId,
          routingPolicyId, correlationId, responseBody }
      ← (fire-and-forget)
```
