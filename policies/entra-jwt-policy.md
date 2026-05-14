# APIM Policy Analysis: `entra-jwt-policy.xml`

This is an **Azure API Management (APIM) policy** that acts as a gateway layer between client applications and Azure OpenAI. It implements authentication, authorization, quota enforcement, request transformation, usage logging, and error handling. The policy executes across all four APIM policy sections: **inbound**, **backend**, **outbound**, and **on-error**.

---

## 1. `<inbound>` — Request Processing (Pre-Backend)

The inbound section performs six distinct operations before the request reaches Azure OpenAI:

### 1.1 JWT Validation

```xml
<validate-jwt header-name="Authorization" ...>
    <openid-config url="https://login.microsoftonline.com/common/.well-known/openid-configuration" />
    <required-claims>
        <claim name="aud" match="all">
            <value>{{ExpectedAudience}}</value>
        </claim>
    </required-claims>
</validate-jwt>
```

- Validates the Bearer token from the `Authorization` header against Entra ID's OpenID Connect metadata.
- Uses `/common/` endpoint — this means it's **multi-tenant**, accepting tokens from any Entra directory.
- Requires the `aud` (audience) claim to match `{{ExpectedAudience}}` (a named value configured in APIM). This ensures tokens are scoped to the correct resource.
- Returns **HTTP 401** on failure with a configurable error message.

### 1.2 Claim Extraction

Extracts three JWT claims into APIM context variables:

| Variable | Claim | Purpose |
|----------|-------|---------|
| `tenantId` | `tid` | Identifies the caller's Entra tenant/organization |
| `clientAppId` | `azp` (delegated) or `appid` (client-credentials) | Identifies the calling application — handles both user-interactive and service-to-service flows |
| `audience` | `aud` | Captured for logging purposes |

The `clientAppId` extraction is notable: it prefers `azp` (set in delegated/on-behalf-of tokens) and falls back to `appid` (set in client-credentials tokens). This supports both authentication flows.

### 1.3 Deployment ID Resolution

```
deploymentId = path segment from /deployments/{id}/...
             OR request body "model" field
             OR last URL segment
```

Three-tier fallback:

1. **URL path** — extracts from `/deployments/{id}/...` (standard Azure OpenAI chat/completions route)
2. **Request body** — reads the `model` field from JSON (for routes like `/responses`, `/models` that use the Responses API)
3. **Last URL segment** — generic fallback

This `deploymentId` is used for per-model quota tracking and usage attribution.

### 1.4 Pre-authorization Check (Precheck)

```xml
<authentication-managed-identity resource="{{ContainerAppAudience}}" ... />
<send-request ... response-variable-name="precheckResponse">
    <set-url>{{ContainerAppUrl}}/api/precheck/{clientAppId}/{tenantId}?deploymentId={deploymentId}</set-url>
</send-request>
```

**This is the core rate-limiting and quota gate.** Before the request reaches OpenAI:

1. APIM acquires a token via its **managed identity** for the Container App audience — no secrets are exchanged.
2. Calls the AI Policy API's `/api/precheck` endpoint, which checks:
   - Client is registered and has a plan assigned
   - Monthly token quota is not exhausted
   - RPM/TPM rate limits are within bounds
3. Response handling:
   - **401** → Client not authorized (no plan assigned) → returns 401 to caller
   - **429** → Quota/rate limit exceeded → returns 429 to caller
   - **Non-200** → Returns 500 to caller
   - **200** → Request proceeds to OpenAI

This **blocks unauthorized or over-quota requests before they incur OpenAI costs** — a key architectural decision.

### 1.5 Auto-Router (Deployment Routing)

After a successful precheck (200), the policy extracts the optional `routedDeployment` field from the precheck response. If `routedDeployment` is present and differs from the client's requested deployment:

1. Saves the original deployment as `originalDeploymentId` for audit logging
2. Updates `deploymentId` to the routed deployment
3. Rewrites the backend URL path (`/deployments/{original}/` → `/deployments/{routed}/`)

If `routedDeployment` is null/empty or matches the requested deployment, no routing occurs — the request passes through unchanged. This is an **auto-router**, not enforced rewriting: users who ask for GPT-4 get GPT-4.

### 1.6 Backend Configuration & Authentication

```xml
<set-backend-service backend-id="openAiBackend" />
<authentication-managed-identity resource="https://cognitiveservices.azure.com/" />
```

- Routes the request to the pre-configured `openAiBackend` backend (defined in `infra/terraform/modules/gateway/main.tf`).
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

Sends a **one-way (fire-and-forget)** POST to the AI Policy API's `/api/log` endpoint with:

| Field | Source |
|-------|--------|
| `tenantId` | JWT `tid` claim |
| `clientAppId` | JWT `azp`/`appid` claim |
| `audience` | JWT `aud` claim |
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
| **Authentication** | Strong — Entra ID JWT validation with audience restriction |
| **Multi-tenant** | `/common` endpoint supports any Entra tenant; tenant isolation is enforced at the data layer via `tenantId` |
| **No API keys** | All backend calls use managed identity — no secrets in policy or config |
| **Pre-authorization** | Requests are gated before reaching OpenAI, preventing cost abuse |
| **APIM-to-backend auth** | Managed identity token for Container App audience — zero-trust between components |

### Considerations

- The `on-error` section exposes detailed error metadata in HTTP headers (source, path, policyId). This is useful for debugging but in production you may want to limit this to avoid leaking internal implementation details to external callers.
- The fire-and-forget log payload is constructed via string interpolation (`$"{{...}}"`). If `requestBody` or `parsedResponseString` contain unescaped JSON, the payload is well-formed because they're already JSON strings. However, if either is empty or malformed, the `/api/log` endpoint receives invalid JSON — the backend handles this gracefully with a `BadRequest` response.
- The `validate-jwt` policy does not restrict `iss` (issuer) claims beyond what `/common` provides. Any Entra tenant can authenticate — access control is delegated to the precheck endpoint which verifies the `clientAppId:tenantId` pair is registered.

---

## 6. Named Values (APIM Configuration)

The policy references the following `{{named values}}` that must be configured in the APIM instance:

| Named Value | Purpose |
|-------------|---------|
| `{{ExpectedAudience}}` | The `aud` claim value required in JWT tokens (typically the APIM app registration's Application ID URI) |
| `{{ContainerAppAudience}}` | The resource/audience used when acquiring a managed identity token for the Container App |
| `{{ContainerAppUrl}}` | Base URL of the AI Policy API running on Azure Container Apps |

---

## 7. Flow Summary

```
Client Request
    │
    ▼
┌─────────────────────────────────────────┐
│  INBOUND                                │
│  1. Validate JWT (Entra ID, audience)   │
│  2. Extract claims (tid, appid/azp)     │
│  3. Resolve deploymentId                │
│  4. Precheck → Container App            │
│     ├─ 401 → block (not authorized)     │
│     ├─ 429 → block (over quota)         │
│     └─ 200 → continue                   │
│  5. Auto-router: if routedDeployment    │
│     differs, rewrite backend URL        │
│  6. Set backend → openAiBackend         │
│  7. Auth via managed identity           │
│  8. Inject stream_options if streaming  │
└───────────────┬─────────────────────────┘
                ▼
         Azure OpenAI
                │
                ▼
┌─────────────────────────────────────────┐
│  OUTBOUND                               │
│  1. Parse response (SSE or JSON)        │
│  2. Fire-and-forget POST to /api/log    │
│     (tenantId, clientAppId, usage)      │
└───────────────┬─────────────────────────┘
                ▼
         Response to Client (unmodified)
```

This is a chargeback/metering gateway policy. The inbound precheck blocks unauthorized or over-quota requests before they incur OpenAI costs, and the outbound fire-and-forget logging ensures usage tracking doesn't add latency to client responses.
