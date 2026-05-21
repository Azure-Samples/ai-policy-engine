# Decision: Non-AI APIM Policy Contract

**Date:** 2026-05-21  
**Author:** Sydnor (Infra/DevOps)  
**Status:** Draft — sample policy created, coordinator alignment still needed  
**Requested by:** Zack Way  

---

## Summary

A draft APIM policy now exists for non-AI REST APIs at `policies/entra-jwt-rest-policy.xml`.

### Default sample contract

- Entra JWT validation mirrors the existing `entra-jwt-policy.xml` pattern.
- Customer identity is derived as `customerKey = {clientAppId}:{tenantId}`.
- Short-window enforcement uses native APIM `rate-limit-by-key`.
- Monthly enforcement uses native APIM `quota-by-key` with a 30-day fixed window.
- The backend is routed with `https://{{NonAiBackendUrl}}`.
- Outbound accounting is always fire-and-forget to `POST {{ContainerAppUrl}}/api/log-rest`.

### Commented alternative already included in the XML

- `GET {{ContainerAppUrl}}/api/precheck-rest/{clientAppId}?tenantId={tenantId}`
- APIM managed identity token in `Authorization: Bearer {token}`
- APIM returns the policy-engine body on 429

This means the policy file is switchable without redesigning the rest of the contract.

---

## Required APIM named values

| Named value | Purpose |
|---|---|
| `EntraTenantId` | Shared Entra-policy-family named value retained for consistency/future hardening |
| `ExpectedAudience` | Required `aud` claim for caller JWTs |
| `NonAiRequestsPerMinute` | Native APIM short-window call limit |
| `NonAiMonthlyRequestQuota` | Native APIM 30-day quota limit |
| `NonAiBackendUrl` | Protected REST backend host/FQDN |
| `ContainerAppUrl` | Policy-engine base URL |
| `ContainerAppAudience` | Managed identity audience/resource for policy-engine calls |

---

## Stable backend target

### `/api/log-rest`

The sample policy always emits a fire-and-forget POST to `/api/log-rest` with this payload shape:

```json
{
  "tenantId": "...",
  "clientAppId": "...",
  "customerKey": "{clientAppId}:{tenantId}",
  "requestPath": "/some/path",
  "statusCode": 200,
  "latencyMs": 37,
  "correlationId": "..."
}
```

Backend implication: Freamon can implement `/api/log-rest` against this payload now, regardless of whether the team keeps native APIM enforcement or switches to precheck-rest.

### Optional `/api/precheck-rest`

If the coordinator chooses centralized enforcement later, the XML already expects:

- Method: `GET`
- Route: `/api/precheck-rest/{clientAppId}`
- Query: `tenantId={tenantId}`
- Auth: APIM managed identity bearer token for `{{ContainerAppAudience}}`
- Response: `200` to allow, `429` to block

The commented block intentionally keeps the contract minimal.

---

## APIM-specific constraints discovered during drafting

1. **`quota-by-key` uses a fixed window, not a calendar month.**
   - The sample uses `renewal-period="2592000"` (30 days).
   - Exact billing-period or calendar-month alignment requires custom counters outside native APIM quota.

2. **`quota-by-key` cannot take runtime expressions for `calls` or `renewal-period`.**
   - Deployment automation can render/import the policy with concrete values.
   - A request-time `send-request` config lookup cannot directly feed `quota-by-key`.

3. **Native APIM counters live in APIM, not Redis.**
   - Dashboards built from `/api/log-rest` are derived/aggregated metrics, not authoritative real-time counter state.

4. **Native APIM response codes differ by policy.**
   - `rate-limit-by-key` blocks with `429`.
   - `quota-by-key` blocks with `403`.

---

## Coordination note

McNulty's current inbox architecture proposal prefers `/api/precheck-rest` as the **primary** model and rejects pure APIM-side enforcement. This draft intentionally ships the requested hybrid/native-default XML anyway, but the coordinator should reconcile:

- **Primary in sample:** native APIM `rate-limit-by-key` + `quota-by-key`
- **Primary in McNulty proposal:** policy-engine `/api/precheck-rest`
- **Backend placeholder difference:** this sample uses `NonAiBackendUrl`; McNulty's draft mentions `NonAiBackendId`

The XML already includes the alternative block so the team can pivot with minimal policy churn once the final architecture decision lands.
