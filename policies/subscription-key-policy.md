# APIM Policy Analysis: `subscription-key-policy.xml`

This is an **Azure API Management (APIM) policy** that acts as a gateway layer between client applications and Azure OpenAI. Unlike the Entra JWT policy, it uses **APIM subscription keys** for client identification instead of OAuth2/JWT tokens. It implements client identification, quota enforcement, request transformation, usage logging, and error handling across all four APIM policy sections: **inbound**, **backend**, **outbound**, and **on-error**.

---

## 1. `<inbound>` — Request Processing (Pre-Backend)

The inbound section performs six distinct operations before the request reaches Azure OpenAI:

### 1.1 Subscription Key Validation & Client Identification

```xml
<set-variable name="tenantId" value="key-based" />
<set-variable name="clientAppId" value="@(context.Subscription.Name)" />
<set-variable name="audience" value="subscription-key" />
```

- **No explicit JWT validation** — authentication is handled natively by APIM's built-in subscription key mechanism (`subscriptionRequired=true` on the API/product). APIM validates the `Ocp-Apim-Subscription-Key` header (or query parameter) automatically before the policy executes.
- `tenantId` is hard-coded to `"key-based"` since subscription keys are not tied to an Entra tenant.
- `clientAppId` is derived from the **subscription name** (`context.Subscription.Name`), which acts as the client identifier for quota tracking and usage attribution.
- `audience` is set to `"subscription-key"` to distinguish this authentication flow from JWT-based flows in downstream logging.

### 1.2 Request Body Capture

```xml
<set-variable name="requestBody" value="@(context.Request.Body.As<string>(preserveContent: true))" />
```

Captures the raw request body as a string with `preserveContent: true`, ensuring the body stream remains available for subsequent reads (backend forwarding, body transformation).

### 1.3 Deployment ID Resolution

```
deploymentId = path segment from /deployments/{id}/...
             OR request body "model" field
             OR last URL segment
```

Three-tier fallback (identical to the Entra JWT policy):

1. **URL path** — extracts from `/deployments/{id}/...` (standard Azure OpenAI chat/completions route)
2. **Request body** — reads the `model` field from JSON (for routes like `/responses`, `/models` that use the Responses API)
3. **Last URL segment** — generic fallback

This `deploymentId` is used for per-model quota tracking and usage attribution.

### 1.4 Pre-authorization Check (Precheck)

```xml
<authentication-managed-identity resource="{{ContainerAppAudience}}" output-token-variable-name="msi-access-token" ignore-error="false" />
<send-request ... response-variable-name="precheckResponse">
    <set-url>{{ContainerAppUrl}}/api/precheck/{clientAppId}/{tenantId}?deploymentId={deploymentId}</set-url>
</send-request>
```

**This is the core rate-limiting and quota gate.** Before the request reaches OpenAI:

1. APIM acquires a token via its **managed identity** for the Container App audience — no secrets are exchanged.
2. Calls the Chargeback API's `/api/precheck` endpoint, which checks:
   - Client is registered and has a plan assigned
   - Monthly token quota is not exhausted
   - RPM/TPM rate limits are within bounds
3. Response handling:
   - **401** → Client not authorized (no plan assigned) → returns 401 to caller
   - **429** → Quota/rate limit exceeded → returns 429 to caller
   - **Non-200** → Returns 500 to caller
   - **200** → Request proceeds to OpenAI

The precheck also handles `null` responses (e.g., network failure reaching the Container App) by returning 401 with a descriptive error message.

### 1.4 Auto-Router (Deployment Routing)

After a successful precheck (200), the policy extracts the optional `routedDeployment` field from the precheck response. If `routedDeployment` is present and differs from the client's requested deployment:

1. Saves the original deployment as `originalDeploymentId` for audit logging
2. Updates `deploymentId` to the routed deployment
3. Rewrites the backend URL path (`/deployments/{original}/` → `/deployments/{routed}/`)

If `routedDeployment` is null/empty or matches the requested deployment, no routing occurs — the request passes through unchanged. This is an **auto-router**, not enforced rewriting: users who ask for GPT-4 get GPT-4.

### 1.5 Backend Configuration & Authentication

```xml
<set-backend-service id="apim-generated-policy" backend-id="openAiBackend" />
<authentication-managed-identity resource="https://cognitiveservices.azure.com/" />
```

- Routes the request to the pre-configured `openAiBackend` backend (defined in `infra/bicep/apimOaiApi.bicep`).
- Authenticates to Azure OpenAI via APIM's **managed identity** with the Cognitive Services resource scope — **no API keys are used**.

### 1.7 Request Body Transformation

```xml
<set-body>@{
    ...
    if (requestBody["stream"] == true) {
        requestBody["stream_options"] = { "include_usage": true };
    }
    return requestBody.ToString();
}</set-body>
```

If the request has `"stream": true`, it injects `"stream_options": {"include_usage": true}`. This forces Azure OpenAI to include token usage data in the final streaming chunk, which is essential for accurate billing of streaming responses.

---

## 2. `<backend>` — Pass-Through

```xml
<backend>
    <base />
</backend>
```

No custom logic — inherits default behavior. The request is forwarded to Azure OpenAI as configured in the inbound section.

---

## 3. `<outbound>` — Response Processing (Post-Backend)

### 3.1 Response Parsing

The policy handles both **non-streaming** and **streaming (SSE)** responses:

- **Non-streaming**: Parses the entire response body as JSON.
- **Streaming**: Identifies `data:` prefixed lines (Server-Sent Events), finds the **last chunk** containing a JSON payload (excluding `[DONE]`), and parses it. This last chunk is where Azure OpenAI places the `usage` object (enabled by the inbound `stream_options` injection).

### 3.2 Fire-and-Forget Usage Logging

```xml
<send-one-way-request mode="new">
    <set-url>{{ContainerAppUrl}}/api/log</set-url>
    ...
</send-one-way-request>
```

Sends a **one-way (fire-and-forget)** POST to the Chargeback API's `/api/log` endpoint with:

| Field | Source |
|-------|--------|
| `tenantId` | `"key-based"` (static) |
| `clientAppId` | APIM subscription name |
| `audience` | `"subscription-key"` (static) |
| `requestBody` | Original client request |
| `responseBody` | Parsed OpenAI response (with usage data) |
| `deploymentId` | Extracted model/deployment name (may be routed) |
| `requestedDeploymentId` | Original deployment the client requested |
| `routedDeployment` | Deployment recommended by precheck (empty if no routing) |

**Key design choice**: `send-one-way-request` means the client gets the OpenAI response immediately without waiting for logging to complete. This eliminates latency overhead on the response path.

---

## 4. `<on-error>` — Error Handling

Captures detailed error information into HTTP response headers:

| Header | Value |
|--------|-------|
| `ErrorSource` | Which component failed |
| `ErrorReason` | Error reason code |
| `ErrorMessage` | Human-readable error message |
| `ErrorScope` | Policy scope where error occurred |
| `ErrorSection` | Which section (inbound/outbound/backend) |
| `ErrorPath` | URL path that triggered the error |
| `ErrorPolicyId` | Specific policy element that failed |
| `ErrorStatusCode` | HTTP status code |

---

## 5. Security Analysis

| Aspect | Assessment |
|--------|-----------|
| **Authentication** | APIM subscription key — validated automatically by the platform before policy executes |
| **Multi-tenant** | Not applicable — `tenantId` is fixed to `"key-based"`; all subscription-key clients share a single logical tenant |
| **No OpenAI API keys** | Backend calls to Azure OpenAI use managed identity — no secrets in policy or config |
| **Pre-authorization** | Requests are gated before reaching OpenAI, preventing cost abuse |
| **APIM-to-backend auth** | Managed identity token for Container App audience — zero-trust between components |

### Considerations

- **Subscription key security**: Subscription keys are static shared secrets. Unlike JWT tokens, they don't expire automatically. Key rotation must be managed manually through APIM. If a key is compromised, the associated subscription must be regenerated.
- **Client identity granularity**: The `clientAppId` is the subscription name, which is an APIM-level administrative label. This is coarser than Entra-based identification where each application registration has a unique `appid`. Multiple users sharing a subscription key are indistinguishable.
- The `on-error` section exposes detailed error metadata in HTTP headers (source, path, policyId). This is useful for debugging but in production you may want to limit this to avoid leaking internal implementation details to external callers.
- The fire-and-forget log payload is constructed via string interpolation. If `requestBody` or `parsedResponseString` contain unescaped JSON, the payload is well-formed because they're already JSON strings. However, if either is empty or malformed, the `/api/log` endpoint receives invalid JSON — the backend handles this gracefully with a `BadRequest` response.

---

## 6. Named Values (APIM Configuration)

The policy references the following `{{named values}}` that must be configured in the APIM instance:

| Named Value | Purpose |
|-------------|---------|
| `{{ContainerAppAudience}}` | The resource/audience used when acquiring a managed identity token for the Container App |
| `{{ContainerAppUrl}}` | Base URL of the Chargeback API running on Azure Container Apps |

> **Note**: Unlike the Entra JWT policy, this policy does **not** require `{{ExpectedAudience}}` since there is no JWT validation.

---

## 7. Comparison with Entra JWT Policy

| Feature | Entra JWT Policy | Subscription Key Policy |
|---------|-----------------|------------------------|
| **Authentication** | Entra ID JWT (OAuth2) | APIM subscription key |
| **Client identification** | `azp`/`appid` JWT claim | `context.Subscription.Name` |
| **Tenant identification** | `tid` JWT claim | Hard-coded `"key-based"` |
| **Token expiry** | Automatic (JWT lifetime) | Manual key rotation |
| **Multi-tenant support** | Yes (any Entra directory) | No (single logical tenant) |
| **Named values required** | 3 (`ExpectedAudience`, `ContainerAppAudience`, `ContainerAppUrl`) | 2 (`ContainerAppAudience`, `ContainerAppUrl`) |
| **Precheck & logging** | Identical | Identical |
| **Backend auth** | Identical (managed identity) | Identical (managed identity) |
| **Stream handling** | Identical | Identical |

---

## 8. Flow Summary

```
Client Request (with Ocp-Apim-Subscription-Key header)
    │
    ▼
┌──────────────────────────────────────────────┐
│  APIM Platform                               │
│  • Validate subscription key (automatic)     │
└───────────────┬──────────────────────────────┘
                ▼
┌──────────────────────────────────────────────┐
│  INBOUND                                     │
│  1. Set identity vars (subscription name)    │
│  2. Capture request body                     │
│  3. Resolve deploymentId                     │
│  4. Precheck → Container App                 │
│     ├─ 401 → block (not authorized)          │
│     ├─ 429 → block (over quota)              │
│     └─ 200 → continue                        │
│  5. Auto-router: if routedDeployment         │
│     differs, rewrite backend URL             │
│  6. Set backend → openAiBackend              │
│  7. Auth via managed identity                │
│  8. Inject stream_options if streaming       │
└───────────────┬──────────────────────────────┘
                ▼
         Azure OpenAI
                │
                ▼
┌──────────────────────────────────────────────┐
│  OUTBOUND                                    │
│  1. Parse response (SSE or JSON)             │
│  2. Fire-and-forget POST to /api/log         │
│     (subscription name, usage data)          │
└───────────────┬──────────────────────────────┘
                ▼
         Response to Client (unmodified)
```

This policy variant provides a simpler onboarding path for clients that cannot integrate with Entra ID (e.g., third-party tools, quick prototyping). It shares the same chargeback/metering backend as the Entra JWT policy, ensuring consistent quota enforcement and usage tracking regardless of the authentication method.
