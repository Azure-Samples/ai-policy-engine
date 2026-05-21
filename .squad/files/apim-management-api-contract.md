# APIM Management API Contract

Auth: every endpoint requires the `AIPolicy.Admin` app role via existing `AdminPolicy` authorization.

## GET /api/apim/apis
- 200 OK
```json
{
  "apis": [
    {
      "id": "azure-openai-jwt-based-api",
      "displayName": "Azure OpenAI Service API",
      "path": "openai",
      "serviceUrl": "https://contoso.openai.azure.com",
      "isCurrent": true
    }
  ]
}
```

## GET /api/apim/apis/{apiId}/operations
- 200 OK
```json
{
  "operations": [
    {
      "id": "chat-completions",
      "displayName": "Chat Completions",
      "method": "POST",
      "urlTemplate": "/deployments/{deploymentId}/chat/completions"
    }
  ]
}
```

## GET /api/apim/apis/{apiId}/policy
## GET /api/apim/apis/{apiId}/operations/{operationId}/policy
- 200 OK
```json
{
  "assignment": {
    "id": "pa:azure-openai-jwt-based-api:_all",
    "apiId": "azure-openai-jwt-based-api",
    "operationId": null,
    "apiDisplayName": "Azure OpenAI Service API",
    "templateId": "entra-jwt-ai",
    "templateVersion": "1.0",
    "parameters": {
      "ExpectedAudience": "api://abc123",
      "ContainerAppUrl": "https://engine.contoso.com",
      "ContainerAppAudience": "api://def456"
    },
    "generatedXmlHash": "sha256:abcdef0123456789",
    "lastAppliedAt": "2026-05-16T10:00:00Z",
    "appliedBy": "user@contoso.com",
    "status": "synced",
    "errorMessage": null,
    "createdAt": "2026-05-16T09:55:00Z",
    "updatedAt": "2026-05-16T10:00:00Z"
  },
  "currentXml": "<policies>...</policies>"
}
```
- `assignment` may be `null` when the engine has no saved assignment for the API/operation.
- `currentXml` is the raw APIM policy XML at the requested scope; it is an empty string when no explicit scope policy exists.
- `status` values: `pending`, `applying`, `synced`, `failed`.
- `errorMessage` is populated when the last async apply failed.

## GET /api/apim/templates
- 200 OK
```json
{
  "templates": [
    {
      "id": "entra-jwt-ai",
      "displayName": "Entra JWT — AI",
      "version": "1.0",
      "scope": "api",
      "parameters": [
        {
          "name": "ExpectedAudience",
          "type": "string",
          "required": true,
          "description": "Expected aud claim in incoming tokens",
          "default": null
        },
        {
          "name": "ContainerAppUrl",
          "type": "string",
          "required": true,
          "description": "AI Policy Engine base URL",
          "default": null
        }
      ]
    }
  ]
}
```

## POST /api/apim/apis/{apiId}/policy
## POST /api/apim/apis/{apiId}/operations/{operationId}/policy
Request body:
```json
{
  "templateId": "entra-jwt-ai",
  "parameters": {
    "ExpectedAudience": "api://abc123",
    "ContainerAppUrl": "https://engine.contoso.com",
    "ContainerAppAudience": "api://def456"
  }
}
```
Responses:
- 202 Accepted
```json
{
  "assignmentId": "pa:azure-openai-jwt-based-api:_all",
  "status": "applying"
}
```
- 400 Bad Request for missing template/parameters or render validation failures.
- 404 Not Found when API or operation does not exist in APIM.
- 500 Internal Server Error for unexpected apply orchestration failures.

## DELETE /api/apim/apis/{apiId}/policy
## DELETE /api/apim/apis/{apiId}/operations/{operationId}/policy
- 200 OK
```json
{
  "status": "cleared"
}
```
- Behavior: deletes the saved engine assignment and writes a passthrough APIM policy at the requested scope:
```xml
<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>
```
- API-scope DELETE returns 404 when the APIM API does not exist.
- Operation-scope DELETE returns 404 when the APIM API or operation does not exist.
- 500 Internal Server Error for unexpected clear failures.
