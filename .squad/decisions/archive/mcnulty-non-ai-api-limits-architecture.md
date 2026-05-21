# Architecture: Non-AI API Usage Limits

**Date:** 2026-05-16  
**Author:** McNulty (Lead / Architect)  
**Status:** Proposal — awaiting Zack's approval  
**Requested by:** Zack Way  

---

## Context

The policy engine currently enforces AI-centric limits: token quotas (monthly), token rate limits (per minute), and request rate limits (per minute) — all scoped to OpenAI/Foundry deployments. Zack wants to extend it so a Plan can also enforce limits on **non-AI REST APIs** fronted by APIM. Two limits:

1. **Requests per minute** — short-window throttle
2. **Requests per month** — monthly cap (like `MonthlyTokenQuota` but counting raw HTTP requests)

A single plan can cover BOTH AI and non-AI APIs simultaneously.

---

## 1. Schema

### Decision: Flat fields on `PlanData` (NOT a sub-object)

**Rationale:** The existing schema is flat. `TokensPerMinuteLimit`, `RequestsPerMinuteLimit`, `MonthlyTokenQuota` — all sit at root level. Introducing a nested `NonAiLimits` object would be inconsistent and would force a breaking JSON shape change for the frontend. Flat fields are also simpler for CosmosDB partial updates and Redis hash storage.

### New fields on `PlanData`:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `NonAiRequestsPerMinute` | `int` | `0` | Max non-AI requests per minute per customer. 0 = unlimited. |
| `NonAiMonthlyRequestQuota` | `long` | `0` | Max non-AI requests per billing period. 0 = unlimited. |

### New fields on `PlanCreateRequest`:

| Field | Type | Nullable | Default |
|-------|------|----------|---------|
| `NonAiRequestsPerMinute` | `int?` | Yes | null (maps to 0) |
| `NonAiMonthlyRequestQuota` | `long?` | Yes | null (maps to 0) |

### New fields on `PlanUpdateRequest`:

| Field | Type | Nullable |
|-------|------|----------|
| `NonAiRequestsPerMinute` | `int?` | Yes (null = no change) |
| `NonAiMonthlyRequestQuota` | `long?` | Yes (null = no change) |

### New fields on `ClientPlanAssignment` (usage tracking):

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `NonAiCurrentPeriodRequests` | `long` | `0` | Non-AI requests consumed this billing period. |

### `PlansResponse` — No change

It already returns `List<PlanData>`, so new fields flow through automatically.

### Interaction with existing AI fields

Both sets of limits are **independent**. A plan with `MonthlyTokenQuota = 1M` and `NonAiMonthlyRequestQuota = 10000` enforces both simultaneously. The precheck endpoints are separate (see Enforcement below), so there's no collision. The billing period is shared — `CurrentPeriodStart` governs rollover for both AI and non-AI counters.

---

## 2. Enforcement Model

### Decision: Option A — New `/api/precheck-rest/{clientAppId}/{tenantId}` endpoint

**Rejected alternatives:**

- **Option B (extend existing precheck with discriminator):** Pollutes the AI hot path. The existing precheck does deployment extraction, routing evaluation, TPM counters — none of which apply to non-AI. Mixing concerns increases latency and complexity.
- **Option C (pure APIM-side enforcement):** Loses dashboard visibility. The whole point of this engine is centralized observability + chargeback. If APIM does enforcement alone, the dashboard can't show non-AI usage trends, and there's no audit trail.

**Why Option A:**

1. Clean separation — non-AI precheck does exactly two things: check rate limit, check monthly quota.
2. Dashboard visibility — the engine sees every non-AI request (incrementing counters in Redis), enabling usage dashboards.
3. Consistent APIM contract — same pattern as AI precheck (APIM calls backend, gets 200/429, acts accordingly). Sydnor's policy is a near-copy.
4. The endpoint is fast — two Redis operations (INCR + GET), no routing/token logic.

**Endpoint contract:**

```
GET /api/precheck-rest/{clientAppId}/{tenantId}
Authorization: Bearer {managed-identity-token}

Response 200:
{
  "status": "authorized",
  "clientAppId": "...",
  "tenantId": "...",
  "plan": "...",
  "currentRpm": 5,
  "rpmLimit": 100,
  "currentMonthlyRequests": 4500,
  "monthlyRequestLimit": 10000
}

Response 429:
{
  "error": "Rate limit exceeded — non-AI requests per minute" | "Non-AI monthly request quota exceeded",
  "limit": ...,
  "current": ...
}

Response 401: Client not authorized (no plan assigned)
```

---

## 3. Counter Storage

### Decision: Redis counters with billing-period tracking in Cosmos (existing pattern)

| Counter | Storage | Mechanism |
|---------|---------|-----------|
| Requests/minute | Redis | `INCR` on key `ratelimit:nonai-rpm:{clientAppId}:{tenantId}:{minuteWindow}` with 120s TTL (same pattern as AI RPM) |
| Requests/month | Redis (hot) + Cosmos (durable) | Increment `NonAiCurrentPeriodRequests` on `ClientPlanAssignment`. Redis cache updated immediately; Cosmos synced on log ingest (same as `CurrentPeriodUsage` pattern). |

**Why not APIM built-in `rate-limit-by-key`?**

- No dashboard visibility
- No billing-period-aware monthly counter
- Can't share state across multiple APIM instances in different regions (Redis is centralized)

**Billing period rollover:** Same logic as AI — `BillingPeriodCalculator.GetCurrentPeriodStartUtc()` detects new period; precheck treats `NonAiCurrentPeriodRequests` as 0 when period has rolled.

### New Redis key:

```csharp
// In RedisKeys.cs:
public static string RateLimitNonAiRpm(string clientAppId, string tenantId, long minuteWindow)
    => $"ratelimit:nonai-rpm:{clientAppId}:{tenantId}:{minuteWindow}";
```

---

## 4. APIM Policy Contract

Sydnor will create `policies/entra-jwt-rest-policy.xml` (and optionally a subscription-key variant). The policy shape:

1. **`<validate-jwt>`** — Same Entra ID validation as AI policy (multi-tenant, audience check)
2. **Extract claims** — `tenantId`, `clientAppId` (same `<set-variable>` blocks)
3. **`<authentication-managed-identity>`** — Get MSI token for Container App
4. **`<send-request>` to `/api/precheck-rest/{clientAppId}/{tenantId}`** — Same pattern as AI precheck call
5. **`<choose>` on response** — 401/403/429 → return to caller. 200 → proceed.
6. **`<set-backend-service>`** — Route to the actual non-AI backend (configurable via named value `{{NonAiBackendId}}`)
7. **Outbound: `<send-one-way-request>` to `/api/log-rest`** — Fire-and-forget usage log (records the request for dashboard/audit). Lightweight payload: `{ clientAppId, tenantId, timestamp, apiPath, statusCode }`.

**Key difference from AI policy:** No `deploymentId` extraction, no routing evaluation, no response token parsing. Much simpler.

**New log endpoint (Freamon):** `POST /api/log-rest` — Accepts non-AI request metadata, increments `NonAiCurrentPeriodRequests` on the client assignment. Same fire-and-forget pattern as AI `/api/log`.

---

## 5. Frontend Contract

Kima adds to the Plan create/edit form:

### New form section: "Non-AI API Limits"

| Field | Input type | Label | Validation |
|-------|-----------|-------|------------|
| `NonAiRequestsPerMinute` | Number input | "Requests per Minute (Non-AI)" | Integer ≥ 0. 0 = unlimited. |
| `NonAiMonthlyRequestQuota` | Number input | "Monthly Request Quota (Non-AI)" | Integer ≥ 0. 0 = unlimited. |

**Placement:** After the existing "Rate Limits" section, before "Routing". Grouped under a visual section header "Non-AI API Limits".

**No toggle needed.** If both fields are 0, non-AI limits are effectively disabled. This matches the pattern for `TokensPerMinuteLimit` (0 = unlimited). A toggle adds unnecessary state.

**Client detail view:** Show `NonAiCurrentPeriodRequests` usage alongside existing token usage in the customer dashboard.

---

## 6. Backward Compatibility

### Decision: Zero-value defaults, no schema versioning needed

- **Existing plans in Cosmos** don't have these fields. When deserialized, .NET assigns default values (`0` for int/long). This means: existing plans have non-AI limits disabled (0 = unlimited = no enforcement). This is correct — no existing plan suddenly gets rate-limited.
- **`NonAiCurrentPeriodRequests`** on `ClientPlanAssignment` defaults to `0`. Existing documents missing this field deserialize cleanly.
- **No migration script needed.** CosmosDB is schema-less. New fields appear when plans are updated via the API.
- **API contract is additive only** — new optional fields on create/update requests. Existing clients that don't send these fields get default behavior (no non-AI limits).

---

## 7. Test Scope

Bunk must cover:

### New code paths (MUST):

1. **Precheck-rest endpoint — rate limit enforced:** Send N+1 requests in same minute window → Nth returns 200, N+1th returns 429 with correct error.
2. **Precheck-rest endpoint — monthly quota enforced:** Client at quota → returns 429. Client below quota → returns 200.
3. **Precheck-rest endpoint — billing period rollover:** Client at quota, but period has rolled → returns 200 (counter resets).
4. **Precheck-rest endpoint — unauthorized client:** No plan assignment → 401.
5. **Precheck-rest endpoint — 0 = unlimited:** Plan with `NonAiRequestsPerMinute = 0` → never rate-limited. Same for monthly.
6. **Log-rest endpoint:** Increments `NonAiCurrentPeriodRequests` correctly.
7. **Plan CRUD:** Create plan with non-AI fields → read back → fields present. Update only non-AI fields → other fields unchanged.

### Regression (MUST):

8. **AI precheck unaffected:** Existing AI precheck tests still pass — no changes to that endpoint.
9. **Plan serialization roundtrip:** Plans with both AI and non-AI fields serialize/deserialize correctly.
10. **Billing period calculator:** Shared logic still works for both AI and non-AI counters.

### Integration (SHOULD, if time allows):

11. **Redis key isolation:** Non-AI RPM keys don't collide with AI RPM keys.
12. **Concurrent requests:** Multiple simultaneous precheck-rest calls correctly increment without race conditions (Redis INCR is atomic).

---

## Summary of Assignments

| Agent | Scope |
|-------|-------|
| **Freamon** | Add fields to `PlanData`, `PlanCreateRequest`, `PlanUpdateRequest`, `ClientPlanAssignment`. Implement `/api/precheck-rest/{clientAppId}/{tenantId}` endpoint. Implement `/api/log-rest` endpoint. Add `RedisKeys.RateLimitNonAiRpm`. Wire DI. |
| **Kima** | Add "Non-AI API Limits" section to Plan form (create + edit). Show `NonAiCurrentPeriodRequests` in client detail. |
| **Sydnor** | Create `policies/entra-jwt-rest-policy.xml` following the contract above. Optionally `policies/subscription-key-rest-policy.xml`. |
| **Bunk** | Write tests covering all 12 scenarios above. |

---

## Open Questions (None blocking — these are future scope)

- **Per-API granularity:** Should different non-AI APIs within the same plan have different limits? (Answer: Not now. Start with plan-level limits. If needed later, add an `ApiLimits` dictionary keyed by API identifier.)
- **Overbilling for non-AI:** Should there be an `AllowNonAiOverbilling` + `NonAiOverageRate`? (Answer: Defer. Start with hard cap. Overbilling is a Phase 2 feature if customers request it.)
