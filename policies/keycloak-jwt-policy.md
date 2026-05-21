# APIM Policy Analysis: `keycloak-jwt-policy.xml`

This is an **Azure API Management (APIM) policy** that validates Keycloak-issued JWT tokens instead of Azure AD tokens. It is functionally equivalent to `entra-jwt-policy.xml` but uses Keycloak's OpenID Connect endpoints and client_credentials flow for backend authentication.

---

## Prerequisites

### APIM Named Values

| Named Value | Description |
|-------------|-------------|
| `KeycloakOpenIdConfigUrl` | Keycloak OIDC discovery URL (e.g., `https://<host>/realms/<realm>/.well-known/openid-configuration`) |
| `KeycloakTokenEndpoint` | Keycloak token endpoint (e.g., `https://<host>/realms/<realm>/protocol/openid-connect/token`) |
| `ExpectedAudience` | The expected `aud` claim in the JWT token |
| `AIPolicyEngineApiBaseUrl` | Base URL of the AI Policy Engine API |
| `AIPolicyEngineApimClientId` | Keycloak client ID for APIM's service account |
| `AIPolicyEngineApimClientSecret` | Keycloak client secret for APIM's service account (store as Secret named value) |

---

## Key Differences from `entra-jwt-policy.xml`

### 1. JWT Validation

Uses Keycloak's OIDC discovery endpoint instead of Azure AD's `/common/.well-known/openid-configuration`.

### 2. Claim Extraction

| Variable | Keycloak Claim | Notes |
|----------|---------------|-------|
| `tenantId` | `tenant_id` (custom) or extracted from issuer URL | Keycloak doesn't have a native `tid` claim; uses custom mapper or parses realm from issuer |
| `clientAppId` | `azp` or `client_id` | `azp` for authorization code flow, `client_id` for client_credentials |
| `audience` | `aud` | Same as Entra |

### 3. Backend Authentication

Instead of Azure Managed Identity, this policy acquires a token from Keycloak using the **client_credentials** grant:

```xml
<send-request mode="new" response-variable-name="keycloakTokenResponse" ...>
    <set-url>{{KeycloakTokenEndpoint}}</set-url>
    <set-method>POST</set-method>
    <set-body>grant_type=client_credentials&client_id=...&client_secret=...</set-body>
</send-request>
```

The resulting access token is used for both the pre-check call and the fire-and-forget log call.

### 4. Named Value References

- `{{ContainerAppUrl}}` → `{{AIPolicyEngineApiBaseUrl}}`
- `{{ContainerAppAudience}}` → removed (token acquired via client_credentials)
- Managed identity → Keycloak client_credentials token

---

## Keycloak Setup Requirements

1. **Create a client** for the AI Policy Engine API (the resource server / audience)
2. **Create a client** for APIM with:
   - `Service accounts roles` enabled (for client_credentials)
   - Appropriate roles/scopes to access the API
3. **Add a custom protocol mapper** (optional) to include `tenant_id` in tokens if multi-tenancy is needed
4. **Configure audience mapper** on the API client to ensure `aud` claim is present in tokens
