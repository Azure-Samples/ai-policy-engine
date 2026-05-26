# APIM Policy Analysis: `entra-jwt-rest-policy.xml`

This policy protects a non-AI REST backend with **Entra JWT validation**, **native APIM request throttling/quota**, and **fire-and-forget usage logging** back to the AI Policy Engine. It mirrors the existing Entra JWT policy style, but removes AI-specific deployment routing and token accounting.

---

## Overview

### Default enforcement path (this sample)

- **Authentication:** Validate the caller's Entra access token.
- **Identity extraction:** Read `tenantId` and `clientAppId`, then build `customerKey = {clientAppId}:{tenantId}`.
- **Short-window protection:** `rate-limit-by-key` enforces requests per minute.
- **Monthly cap:** `quota-by-key` enforces a 30-day fixed-window request quota.
- **Forwarding:** Route to the protected non-AI backend.
- **Accounting:** Post request outcome metadata to `/api/log-rest` without adding latency to the caller.

### Alternative enforcement path (commented in XML)

The XML also includes a commented `send-request` block for a policy-engine-enforced path using `/api/precheck-rest/{clientAppId}`. If the team chooses centralized enforcement later, comment out the native APIM limit policies and uncomment that block.

---

## 1. `<inbound>` — Request Processing

### 1.1 `base`

`<base />` keeps higher-scope APIM policies active, matching the established policy pattern in this repo.

### 1.2 JWT validation

The policy reuses the same validation shape as `entra-jwt-policy.xml`:

- `Authorization` header is required.
- OpenID metadata comes from `https://login.microsoftonline.com/common/.well-known/openid-configuration`.
- The `aud` claim must equal `{{ExpectedAudience}}`.

This keeps the REST policy aligned with the existing multi-tenant Entra gateway behavior.

### 1.3 Claim extraction and customer key

Three variables are created:

| Variable | Source | Purpose |
|---|---|---|
| `tenantId` | `tid` | Caller tenant isolation |
| `clientAppId` | `azp` with `appid` fallback | App identity for delegated and app-only flows |
| `customerKey` | `{clientAppId}:{tenantId}` | Shared APIM counter key + log identity |

`customerKey` is multi-tenant safe: the same app ID in two different tenants produces two different keys.

### 1.4 Native requests-per-minute throttle

```xml
<rate-limit-by-key calls="{{NonAiRequestsPerMinute}}"
                   renewal-period="60"
                   counter-key="@((string)context.Variables[&quot;customerKey&quot;])" />
```

Why native APIM here:

- Fast, gateway-local enforcement.
- No precheck dependency on the hot path.
- Simple operational model for a draft/sample policy.

### 1.5 Native monthly quota

```xml
<quota-by-key calls="{{NonAiMonthlyRequestQuota}}"
              renewal-period="2592000"
              counter-key="@((string)context.Variables[&quot;customerKey&quot;])" />
```

Important trade-off:

- `quota-by-key` uses a **fixed window**, not a true calendar month.
- This sample uses `2592000` seconds (30 days) because APIM cannot calendar-align this natively.
- If the business must reset on exact month boundaries, the team will need custom counters outside native `quota-by-key`.

Also note the HTTP behavior difference:

- `rate-limit-by-key` exceeds to **429**.
- `quota-by-key` exceeds to **403**.

### 1.6 Backend routing

```xml
<set-backend-service base-url="https://{{NonAiBackendUrl}}" />
```

This forwards the request to the protected REST backend. The sample uses a named value so Terraform or deployment automation can point the same policy at different backends.

### 1.7 Managed identity for policy-engine calls

The policy acquires a managed identity token for `{{ContainerAppAudience}}`. That token is used for outbound logging and is also what the commented `/api/precheck-rest` alternative would use.

### 1.8 Supplying the limit values

The XML comments call out two ways to source the native APIM limits:

1. **Default:** use APIM named values (`{{NonAiRequestsPerMinute}}`, `{{NonAiMonthlyRequestQuota}}`).
2. **Generated policy path:** have deployment automation fetch config from the policy engine and render/import the XML with concrete values.

A runtime `send-request` config lookup is **not** enough for `quota-by-key`, because APIM does not allow policy expressions in that policy's `calls` or `renewal-period` attributes.

---

## 2. `<backend>` — Pass-through

```xml
<backend>
    <base />
</backend>
```

No custom backend-stage logic is required.

---

## 3. `<outbound>` — Fire-and-forget logging

The outbound section mirrors the existing Entra policy's pattern by using a non-blocking call to the policy engine:

```xml
<send-one-way-request mode="new">
    <set-url>{{ContainerAppUrl}}/api/log-rest</set-url>
    ...
</send-one-way-request>
```

Payload fields in the sample:

- `clientAppId`
- `tenantId`
- `customerKey`
- `requestPath`
- `statusCode`
- `latencyMs`
- `correlationId`

Because this is a one-way request, the caller does not wait for the log pipeline to finish.

### Accounting implication

The policy engine receives the event stream for dashboards and audit, but **APIM owns the live counters**. That means dashboard totals are derived from logs and may not exactly match APIM's real-time limit state at any instant.

---

## 4. `<on-error>`

The sample keeps `on-error` minimal:

```xml
<on-error>
    <base />
</on-error>
```

That matches the request for a lightweight draft focused on the policy contract.

---

## 5. Named values to configure

Set these named values in APIM before attaching the policy:

| Named value | Purpose |
|---|---|
| `EntraTenantId` | Shared Entra policy-family value already provisioned by Terraform; retained for consistency/future hardening |
| `ExpectedAudience` | Required `aud` claim for incoming tokens |
| `NonAiRequestsPerMinute` | Native APIM per-minute limit |
| `NonAiMonthlyRequestQuota` | Native APIM 30-day quota |
| `NonAiBackendUrl` | Protected REST backend host/FQDN |
| `ContainerAppUrl` | AI Policy Engine base URL |
| `ContainerAppAudience` | Managed identity audience/resource for policy-engine calls |

---

## 6. How to deploy

1. **Create/update the APIM named values** listed above.
2. **Import `policies/entra-jwt-rest-policy.xml`** into the API or operation that fronts the non-AI backend.
3. **Prefer API scope** unless you intentionally want different operations to use different policies.
4. **Do not attach duplicate copies at multiple nested scopes** unless you intentionally want multiple increments.
5. **Verify managed identity access** so APIM can call `{{ContainerAppUrl}}/api/log-rest`.

### Scope guidance

- Attach at **API scope** if the whole REST API should share one request budget per customer.
- Attach at **operation scope** only when a subset of operations should be protected.
- If you reuse the exact same `customerKey` across multiple APIs, APIM will share the counters for that key unless you change the key composition.

---

## 7. Trade-offs vs. policy-engine-enforced precheck

### Native APIM default (this sample)

**Pros**
- Lowest gateway-path latency.
- No precheck dependency before backend forwarding.
- Simple APIM-native operational model.

**Cons**
- Live counters exist only inside APIM.
- Dashboard numbers are derived from logs, not authoritative limit state.
- Monthly quota is fixed-window, not billing-period aware.
- Native quota exhaustion returns **403**, not **429**.

### Policy-engine precheck alternative

**Pros**
- Central counter state in Redis/policy engine.
- Easier to align with billing periods and future chargeback rules.
- Dashboard can reflect enforcement state more directly.

**Cons**
- Adds a hard dependency and latency on the request path.
- Requires `/api/precheck-rest/{clientAppId}` availability on every protected request.
- More moving parts to troubleshoot.

---

## 8. Customer key design

`customerKey = {clientAppId}:{tenantId}`

Why this matters:

- **Tenant-safe:** avoids collisions when the same application ID exists in multiple tenants.
- **Stable:** derived entirely from JWT claims already present in the request.
- **Reusable:** the same key works for APIM counters and downstream accounting logs.

If the team later needs per-API isolation instead of a shared pool, prefix the key with an API identifier when generating the policy.
