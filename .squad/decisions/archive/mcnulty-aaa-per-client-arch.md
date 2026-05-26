# AAA Per-Client Endpoint Authorization Architecture

**Author:** McNulty (Lead / Architect)  
**Date:** 2026-05-21  
**Status:** Proposal — awaiting Zack approval  
**Requested by:** Zack Way  

---

## TL;DR — Consequential Decisions

1. **New "Access Profile" document type.** Per-client, per-endpoint policy bindings stored in Cosmos `configuration` container with partition key `"access-profile"`.
2. **Resolution is a lookup, not a rules engine.** Most-specific match wins: `(client + operation)` > `(client + api)` > `(client + global)` > endpoint default. Deterministic, cacheable, no regex.
3. **Strictly additive to M1-M4 APIM template work.** `PolicyAssignment` doesn't change. Access Profiles sit *above* it — they resolve which Plan/Routing to enforce *when the request arrives*, not what XML is installed.
4. **Client identity is the existing `clientAppId:tenantId` composite key.** No new identity abstraction in v1. Both Entra JWT clients and subscription-key clients use the same key (sub-key clients already use `context.Subscription.Name:key-based`).
5. **AAA naming: "Access Profile."** RADIUS analogy: NAS-Port = API endpoint, User-Name = clientAppId:tenantId, Service-Type = Plan+Routing policy. The Access Profile is the RADIUS Access-Accept record.

---

## 1. Mental Model Confirmation

The three-layer architecture is correct:

| Layer | Responsibility | What Ships Here |
|-------|---------------|-----------------|
| **Transport** (APIM template assignment) | Install XML that calls our REST endpoints with `clientAppId`, `tenantId`, `deploymentId`, body | M1-M4 `PolicyAssignment` — already built. Installs the "wiring." |
| **Authorization** (Access Profiles — THIS PROPOSAL) | Given `(client, apiId, operationId)`, resolve WHICH Plan and WHICH Routing policy apply | New `AccessProfile` doc. New resolution service. New admin endpoints. |
| **Enforcement** (Precheck / Log Ingest) | Given a resolved Plan+Routing, enforce rate limits, quotas, deployment access, route rewrites | Existing `PrecheckEndpoints.cs`, `RoutingEvaluator.cs`, `LogIngestEndpoints.cs` — unchanged. |

**Key insight from reading the code:** Today the enforcement layer gets its Plan from `ClientPlanAssignment.PlanId` — a flat, global binding. There is no concept of "this client gets Plan X for API-A but Plan Y for API-B." The `ClientPlanAssignment` is a **global** client-to-plan binding. Access Profiles add the **endpoint-scoped** override layer on top of that global default.

---

## 2. Data Model — Access Profile

### Document Shape

```json
{
  "id": "ap:{clientAppId}:{tenantId}:{apiId}:{operationId|_all}",
  "partitionKey": "access-profile",
  "clientAppId": "my-app-client-id",
  "tenantId": "contoso-tenant-id",
  "apiId": "azure-openai-jwt-based-api",
  "operationId": null,
  "planId": "enterprise-plan",
  "routingPolicyId": "cost-optimized-routing",
  "allowedDeployments": ["gpt-4o", "gpt-4o-mini"],
  "enabled": true,
  "createdAt": "2026-05-21T10:00:00Z",
  "updatedAt": "2026-05-21T10:00:00Z",
  "createdBy": "admin@contoso.com"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | auto | Composite key: `ap:{clientAppId}:{tenantId}:{apiId}:{operationId\|_all}` |
| `partitionKey` | string | auto | Always `"access-profile"` |
| `clientAppId` | string | yes | From JWT `azp`/`appid` or APIM subscription name |
| `tenantId` | string | yes | From JWT `tid` or `"key-based"` for sub-key clients |
| `apiId` | string | yes | APIM API ID (matches `PolicyAssignment.ApiId`). Use `"_global"` for a client-wide default. |
| `operationId` | string? | no | APIM operation ID. Null = applies to entire API. |
| `planId` | string | yes | Reference to `PlanData.Id` — the billing/quota plan for this scope |
| `routingPolicyId` | string? | no | Reference to `ModelRoutingPolicy.Id`. Null = inherit from plan. |
| `allowedDeployments` | string[] | no | Override deployment allowlist. Empty = inherit from plan. |
| `enabled` | bool | yes | Toggle without deleting |
| `createdAt` | DateTime | auto | |
| `updatedAt` | DateTime | auto | |
| `createdBy` | string | auto | Admin UPN |

### Cosmos Strategy

- **Container:** existing `configuration` (same as Plans, Routing Policies, PolicyAssignments)
- **Partition key:** `"access-profile"` — all profiles in one logical partition for efficient cross-client queries (admin page lists all)
- **ID format:** deterministic composite → enables point reads (fastest Cosmos operation)
- **Expected volume:** low hundreds to low thousands. Single-partition is fine at this scale.

### Relationship Diagram

```
ClientPlanAssignment (global default)
  └── planId → PlanData
  └── modelRoutingPolicyOverride → ModelRoutingPolicy

AccessProfile (endpoint-scoped override)         ← NEW
  └── planId → PlanData
  └── routingPolicyId → ModelRoutingPolicy
  └── clientAppId:tenantId → ClientPlanAssignment (same client)
  └── apiId → PolicyAssignment.ApiId (same API)

PolicyAssignment (APIM template)
  └── templateId → which XML is installed
  └── apiId → APIM API
```

---

## 3. Resolution Algorithm

### Lookup Order (most-specific wins)

When `/api/precheck/{clientAppId}/{tenantId}` is called, the engine resolves the effective policy through this cascade:

1. **Operation-specific profile:** `ap:{clientAppId}:{tenantId}:{apiId}:{operationId}` — exact operation override
2. **API-specific profile:** `ap:{clientAppId}:{tenantId}:{apiId}:_all` — covers all operations on this API
3. **Global client profile:** `ap:{clientAppId}:{tenantId}:_global:_all` — client-wide default
4. **Legacy fallback:** `ClientPlanAssignment.PlanId` — today's global binding (backward-compatible)

**First match wins. No merging. No inheritance between levels.** A profile at level 1 completely determines Plan + Routing for that request; we don't partially inherit from level 4.

### How Precheck Gets `apiId` and `operationId`

**This is the critical integration point.** Today the APIM templates pass `clientAppId` and `tenantId` to precheck. They do NOT pass `apiId` or `operationId`.

**Required change:** The APIM policy templates must pass `apiId` and `operationId` as query parameters to precheck:

```
/api/precheck/{clientAppId}/{tenantId}?deploymentId=gpt-4o&apiId=my-api&operationId=chat-completions
```

The APIM policy has access to `context.Api.Id` and `context.Operation.Id` natively. We add two `set-variable` statements and append them to the precheck URL. **This is a template parameter change, not a structural change** — existing templates continue to work without `apiId`/`operationId` (precheck falls through to level 4).

### Fall-Through Behavior

- If no Access Profile matches AND no `ClientPlanAssignment` exists → **401 Unauthorized** (today's behavior, unchanged)
- If no Access Profile matches BUT `ClientPlanAssignment` exists → use global plan (today's behavior, unchanged)
- If Access Profile exists with `enabled: false` → skip it, fall to next level

### Caching Strategy

- **Redis:** Cache resolved Access Profiles with key `access-profile:{clientAppId}:{tenantId}:{apiId}:{operationId}` and 30-second TTL (same as routing policy cache today)
- **In-memory:** Hot path uses `ConcurrentDictionary` with 30-second TTL (same pattern as `RoutingPolicyCache` in PrecheckEndpoints.cs)
- **Invalidation:** Admin writes invalidate Redis key immediately. In-memory cache expires naturally (30s staleness is acceptable for admin config changes)

### Worked Example

**Setup:**
- Client `app-123:tenant-456` has a global `ClientPlanAssignment` with `PlanId = "basic-plan"`
- An `AccessProfile` exists: `ap:app-123:tenant-456:openai-api:_all` → `planId = "premium-plan", routingPolicyId = "fast-routing"`
- Another `AccessProfile`: `ap:app-123:tenant-456:openai-api:chat-completions` → `planId = "unlimited-plan"`

**Request 1:** Client calls `openai-api`, operation `embeddings`
- Check level 1: `ap:app-123:tenant-456:openai-api:embeddings` → not found
- Check level 2: `ap:app-123:tenant-456:openai-api:_all` → **MATCH** → use `premium-plan` + `fast-routing`

**Request 2:** Client calls `openai-api`, operation `chat-completions`
- Check level 1: `ap:app-123:tenant-456:openai-api:chat-completions` → **MATCH** → use `unlimited-plan`, no routing override (null → inherit from plan)

**Request 3:** Client calls `internal-api`, operation `anything`
- Check level 1: not found
- Check level 2: not found
- Check level 3: `ap:app-123:tenant-456:_global:_all` → not found (none created)
- Check level 4: `ClientPlanAssignment.PlanId = "basic-plan"` → **FALLBACK** → use `basic-plan`

---

## 4. API Surface

All endpoints require `AdminPolicy` authorization. Prefix: `/api/access-profiles`.

```
GET    /api/access-profiles
       Query: ?clientAppId=&tenantId=&apiId=  (all optional filters)
       → 200: { profiles: AccessProfile[] }

GET    /api/access-profiles/{profileId}
       → 200: AccessProfile
       → 404

POST   /api/access-profiles
       Body: { clientAppId, tenantId, apiId, operationId?, planId, routingPolicyId?, allowedDeployments?, enabled? }
       → 201: AccessProfile
       → 409: if exact scope already exists
       → 400: if planId doesn't exist

PUT    /api/access-profiles/{profileId}
       Body: { planId?, routingPolicyId?, allowedDeployments?, enabled? }
       → 200: AccessProfile
       → 404

DELETE /api/access-profiles/{profileId}
       → 204
       → 404

POST   /api/access-profiles/bulk
       Body: { profiles: [{ clientAppId, tenantId, apiId, operationId?, planId, ... }] }
       → 200: { created: int, failed: [{ index, error }] }
       (Admin assigns same plan to multiple clients for an API in one shot)
```

### Client List Source

Clients already exist as `ClientPlanAssignment` documents (partition key `"client"`). The existing `GET /api/clients` endpoint returns them. **No new client identity management needed** — Access Profiles reference the same `clientAppId:tenantId` key that's already in the system.

### Resolution Endpoint (internal — called by precheck, not exposed to UI)

The resolution logic lives in a new `IAccessProfileResolver` service injected into `PrecheckEndpoints`. No separate HTTP endpoint needed — it's an internal service call on the hot path.

---

## 5. UI Implications (Kima)

### Recommended Approach: New `/access` Page

**Not** bolted onto the existing `/apis` page (which is about APIM template assignment — transport layer). Access Profiles are a different concern (authorization layer).

**Layout:**
- **Top:** Client selector (dropdown/search of existing clients from `GET /api/clients`)
- **Main grid:** APIs (rows) × Columns: Plan, Routing Policy, Deployments, Status toggle
- **Drill-down:** Click an API row to expand operations with per-operation overrides
- **Action:** "Assign" button opens a form: select Plan (from existing Plans), optionally select Routing Policy, optionally restrict deployments
- **Bulk action:** "Apply to multiple APIs" — select APIs from checklist, assign same profile

**Alternative considered:** Tree view (APIs → operations → clients). Rejected because the primary workflow is "configure THIS client's access to various APIs" — client-first, not API-first.

**Reuse:** The Plan selector dropdown already exists in the client assignment flow. The Routing Policy selector exists in the Plans page. Kima reuses both.

---

## 6. Intersection with In-Flight APIM Template Work

### `PolicyAssignment` — NO CHANGES NEEDED

The `PolicyAssignment` doc type is purely about which XML template is installed on which APIM API. Access Profiles are orthogonal — they don't change what's installed, they change what gets resolved *when the installed policy calls precheck*.

### Template XML — MINOR ADDITIVE CHANGE

The APIM policy templates need to pass `apiId` and `operationId` to the precheck URL. This is a **backward-compatible addition**:

```xml
<!-- Add after existing set-variable blocks -->
<set-variable name="apiId" value="@(context.Api.Id)" />
<set-variable name="operationId" value="@(context.Operation.Id)" />

<!-- Modify precheck URL to include apiId and operationId -->
<set-url>@((string)context.Variables["containerAppBaseUrl"] + "/api/precheck/" 
    + (string)context.Variables["clientAppId"] + "/" 
    + (string)context.Variables["tenantId"] 
    + "?deploymentId=" + (string)context.Variables["deploymentId"]
    + "&apiId=" + (string)context.Variables["apiId"]
    + "&operationId=" + (string)context.Variables["operationId"])</set-url>
```

**This does NOT require re-applying existing templates.** If `apiId`/`operationId` are absent from the precheck request, the resolver falls through to the global `ClientPlanAssignment` (level 4). Old templates keep working.

### Timing

This work can start immediately. It doesn't block the in-flight PR #32. The template changes ship in the NEXT template version bump (existing `templateVersion: "1.0"` keeps working).

---

## 7. Open Questions for Zack

### Q1: Client Identity Model

Today we have two patterns:
- **Entra JWT clients:** `clientAppId` = JWT `azp`/`appid`, `tenantId` = JWT `tid`
- **Subscription-key clients:** `clientAppId` = APIM subscription name, `tenantId` = `"key-based"`

**Do we keep this dual model, or unify into an engine-owned "client" abstraction?**

My recommendation: Keep the dual model for v1. It works, the precheck endpoint already handles both, and adding an abstraction layer adds complexity without solving a real problem today. Unify in v2 if we need cross-auth-type client identity.

### Q2: Are Routing Rules Always Paired with a Plan?

Can an Access Profile specify ONLY a routing policy override without changing the Plan? Or must every profile have a `planId`?

My recommendation: `planId` is REQUIRED. A routing policy without quota/rate enforcement is meaningless in this engine — you'd get unlimited access. If Zack wants "same plan, different routing," the profile specifies the same planId explicitly + a different routingPolicyId.

### Q3: Drift Detection — Client Lifecycle

If an APIM subscription is deleted (sub-key client) or an Entra app registration is removed, do we:
- (A) Auto-detect and prune Access Profiles? (Requires periodic reconciliation job)
- (B) Leave orphaned profiles (they're inert — precheck just won't match them)?
- (C) Show "stale" badge in UI but don't auto-delete?

My recommendation: (B) for v1 — orphans are harmless. (C) for v2 — surface it in the UI with a manual "clean up" button.

### Q4: Multi-Tenancy — Client Scope

Is a client (`clientAppId:tenantId`) global to the engine, or scoped per-API?

Based on current code: **Global.** A `ClientPlanAssignment` exists once and the same client can hit any API the template is installed on. Access Profiles add per-API scoping on top. This means the same client can have different plans for different APIs — which is exactly what Zack asked for.

**Confirm:** Is there ever a case where the same `clientAppId:tenantId` should mean different things on different APIs? (E.g., subscription "team-alpha" on API-A is a different logical client than "team-alpha" on API-B?) If yes, we need a namespace. I assume no.

### Q5: AAA Naming

Options in RADIUS vernacular:
- **Access Profile** (my recommendation) — maps to RADIUS Access-Accept: "here's what this user gets on this port"
- **Service Authorization** — more formal, maps to Service-Type attribute
- **Network Access Policy** — too generic, confusable with APIM policy
- **Authorization Binding** — accurate but not evocative

**Recommendation: "Access Profile."** Short. Clear. Scannable in UI. Maps cleanly to RADIUS mental model.

### Q6: Default-Deny vs Default-Allow

When a client hits an API that has NO Access Profile AND NO global `ClientPlanAssignment`:
- Today: **401 Unauthorized** (deny)
- Should we add a concept of "API default profile" — a profile with `clientAppId = "_default"` that any unrecognized client falls into?

My recommendation: No for v1. Explicit client registration is a feature, not a bug. If Zack wants "open" APIs, they simply don't install the precheck template on those APIs.

---

## 8. Phasing

| Milestone | Scope | Agent | Depends On |
|-----------|-------|-------|------------|
| **M1** | `AccessProfile` model + Cosmos repository + `IAccessProfileResolver` service with cascade logic | Freamon | None (additive) |
| **M2** | CRUD endpoints (`/api/access-profiles/*`) + bulk assign | Freamon | M1 |
| **M3** | Precheck integration — modify `PrecheckEndpoints.cs` to call resolver when `apiId` query param present; fall through to existing behavior when absent | Freamon | M1 |
| **M4** | Template update — add `apiId`/`operationId` to precheck URL in all 5 templates + version bump | Freamon/Sydnor | M3 (needs endpoint ready to receive) |
| **M5** | UI — `/access` page: client selector, API grid, per-operation drill-down, assign form | Kima | M2 (needs CRUD API) |
| **M6** | Redis caching for resolver hot path + invalidation on write | Freamon | M3 |

### Thin Slice (M1-M3): Ship without UI

M1-M3 can ship behind the existing admin API. The resolver works on the hot path. Admins can CRUD profiles via API (Postman/curl). Kima builds UI in parallel.

**Total estimate:** M1-M3 is ~2 days of Freamon work. M4 is trivial (template string changes). M5 is Kima's standard page (3-4 days based on prior pages).

---

## 9. Risks & Non-Goals

### Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Precheck latency increase (4 Cosmos reads in worst case) | Medium | Redis cache + in-memory cache. Realistic case is 1-2 reads (most clients have a level 2 or level 4 match). Point reads by ID are <5ms in Cosmos. |
| Stale cache serves wrong plan for up to 30s after admin change | Low | Acceptable for admin config changes. Add "changes take up to 30 seconds to propagate" note in UI. |
| Access Profiles with invalid `planId` references | Low | Validate on write (POST/PUT checks plan exists). UI uses dropdown populated from existing plans. |
| Schema migration — existing `ClientPlanAssignment` still works? | None | Fully backward-compatible. If no Access Profile matches, precheck uses `ClientPlanAssignment` exactly as today. Zero migration needed. |

### Non-Goals (v1)

- **Dynamic policy evaluation** (rules engine, ABAC, time-based policies) — out of scope. Deterministic lookups only.
- **Client self-service** — clients can't request their own access. Admin-only.
- **Audit trail for profile changes** — reuse existing audit log pattern but don't build a dedicated "who changed what" timeline.
- **Cross-APIM-instance profiles** — 1:1 engine-to-APIM, same as PolicyAssignment.
- **Inheritance/merging between levels** — first match wins, no partial override. Keeps resolution simple and debuggable.
- **Rate limit pooling across APIs** — each API enforces its own limits independently, even if same plan is used.

---

## Appendix A: Precheck Flow (After This Work)

```
APIM policy XML (transport) 
  → calls /api/precheck/{clientAppId}/{tenantId}?deploymentId=X&apiId=Y&operationId=Z

PrecheckEndpoints.cs:
  1. If apiId present → call AccessProfileResolver.ResolveAsync(clientAppId, tenantId, apiId, operationId)
     → Returns (planId, routingPolicyId, allowedDeployments) or null
  2. If resolver returns a match → load PlanData by resolved planId
  3. If resolver returns null → fall back to ClientPlanAssignment.PlanId (today's path)
  4. Continue with existing quota/rate-limit/routing enforcement (unchanged)
```

## Appendix B: IAccessProfileResolver Interface

```csharp
public interface IAccessProfileResolver
{
    /// <summary>
    /// Resolves the effective access profile for a client+endpoint tuple.
    /// Returns null if no profile matches (caller should fall back to ClientPlanAssignment).
    /// </summary>
    Task<ResolvedAccess?> ResolveAsync(
        string clientAppId, string tenantId, 
        string apiId, string? operationId,
        CancellationToken ct = default);
}

public sealed class ResolvedAccess
{
    public string PlanId { get; set; } = string.Empty;
    public string? RoutingPolicyId { get; set; }
    public List<string> AllowedDeployments { get; set; } = [];
    public string SourceProfileId { get; set; } = string.Empty; // for audit/debug
}
```
