# .NET 10 Deployment Guide

Complete guide for deploying the Azure AI Gateway Policy Engine to Azure.

## Prerequisites

| Requirement | Version | Notes |
|-------------|---------|-------|
| .NET 10 SDK | 10.0+ | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Node.js | 20+ | For building the React dashboard |
| Docker | Latest | For building the container image |
| Azure CLI | 2.50+ | [Install](https://learn.microsoft.com/cli/azure/install-azure-cli) |
| Azure subscription | — | Contributor role required |

**Permissions needed**:
- **Subscription Contributor** — to create resource groups and deploy Terraform infrastructure
- **Entra ID admin** (or Application Administrator role) — to register apps and grant admin consent

---

## Quick Deploy (Automated)

```powershell
git clone https://github.com/your-org/ai-policy-engine.git
cd ai-policy-engine/infra/terraform
cp terraform.tfvars.sample terraform.tfvars  # Edit with your values
terraform init
terraform apply

# Stage 2: Build and deploy the container
cd ../..
./scripts/deploy-container.ps1 -ResourceGroupName rg-aipolicy-eastus2
```

**What the scripts do**:
1. `terraform apply` — provisions all Azure resources (Container App, APIM, CosmosDB, Redis, Key Vault, ACR, etc.)
2. `deploy-container.ps1` — builds the Docker image, pushes to ACR, updates the Container App, and verifies Entra configuration

**Expect**: 30–60 minutes total (APIM provisioning takes the bulk of the time).

**Output**: The script prints the Container App URL, APIM gateway URL, and Entra app IDs when complete.

---

## Manual Deployment Steps

For users who want to deploy step-by-step or need to customize the process.

### 1. Create Resource Group

```bash
az login
az group create --name rg-aipolicyengine --location eastus2
```

### 2. Create ACR and Build Docker Image

```bash
# Create Azure Container Registry
az acr create --resource-group rg-aipolicyengine --name acraipolicyengine --sku Basic --admin-enabled true

# Login to ACR
az acr login --name acraipolicyengine

# Build and push the Docker image from the dotnet/ directory
cd src
docker build -t acraipolicyengine.azurecr.io/aipolicyengine-api:latest .
docker push acraipolicyengine.azurecr.io/aipolicyengine-api:latest
cd ..
```

### 3. Register Entra ID Apps

You need four app registrations (see `scripts/setup-azure.ps1` for the automated path):

1. **AI Policy Engine API** — audience for dashboard UI calls direct to the Container App and APIM → Container App (MI) calls. Hosts `AIPolicy.Export`, `AIPolicy.Admin`, `AIPolicy.Apim` app roles. Exposes `access_as_user` for the dashboard UI.
2. **AI Policy APIM Gateway** — audience for client → APIM tokens (`ExpectedAudience`). Exposes `access_as_user`. Clients request `api://{gatewayAppId}/.default` (or `/access_as_user` for interactive flows).
3. **AI Policy Sample Client** — internal demo client with `access_as_user` delegated permission on the Gateway app, and the `AIPolicy.Admin` app-role on the API app (used for client-credentials plan seeding).
4. **AI Policy Demo Client 2** *(optional)* — external/multi-tenant demo client with `access_as_user` delegated permission on the Gateway app.

#### 3a. API App Registration (Container App audience)

```bash
# Create the API app
az ad app create \
  --display-name "AI Policy Engine API" \
  --identifier-uris "api://aipolicyengine-api" \
  --sign-in-audience "AzureADMyOrg"

# Note the appId from the output — this is your ExpectedAudience
API_APP_ID=$(az ad app list --display-name "AIPolicyEngine API" --query "[0].appId" -o tsv)

# Expose an API scope
az ad app update --id $API_APP_ID \
  --set "api.oauth2PermissionScopes=[{\"id\":\"$(uuidgen)\",\"adminConsentDescription\":\"Access AIPolicyEngine API\",\"adminConsentDisplayName\":\"access_as_user\",\"isEnabled\":true,\"type\":\"User\",\"userConsentDescription\":\"Access AIPolicyEngine API\",\"userConsentDisplayName\":\"access_as_user\",\"value\":\"access_as_user\"}]"
```

#### 3b. Client App Registration

```bash
# Create a client app
az ad app create \
  --display-name "AI Policy Client" \
  --sign-in-audience "AzureADMyOrg"

CLIENT_APP_ID=$(az ad app list --display-name "AI Policy Client" --query "[0].appId" -o tsv)

# Create a client secret (for client_credentials flow)
az ad app credential reset --id $CLIENT_APP_ID --display-name "aipolicy-secret"

# Grant the client app permission to the API scope
az ad app permission add --id $CLIENT_APP_ID \
  --api $API_APP_ID \
  --api-permissions "<scope-id>=Scope"

# Grant admin consent
az ad app permission admin-consent --id $CLIENT_APP_ID
```

#### 3c. SPA Redirect URIs (for Dashboard)

After deployment, you'll need the Container App URL. Come back to this step after step 4.

```bash
# Add SPA redirect URI (replace with your Container App URL)
az ad app update --id $CLIENT_APP_ID \
  --spa-redirect-uris "https://ca-aipolicy.<region>.azurecontainerapps.io"
```

#### Understanding `azp` vs `appid` JWT Claims

The APIM policy extracts the client application ID from the JWT token:
- **Delegated tokens** (user signs in): The client app ID is in the `azp` claim
- **Client credentials tokens** (app-to-app): The client app ID is in the `appid` claim

The policy (`entra-jwt-policy.xml`) handles this automatically with a fallback: it checks `azp` first, then falls back to `appid`.

### 4. Deploy Terraform

```bash
cd infra/terraform
cp terraform.tfvars.sample terraform.tfvars  # Edit with your values
terraform init
terraform apply
```

Note the `container_app_url` from the Terraform output — you'll need it for the next steps.

### 5. Post-Deploy: Fix Redis Connection String

The Terraform `data` module outputs the Redis hostname. Update the Container App env var if needed:

```bash
# Get the Redis access key
REDIS_KEY=$(az redis list-keys --resource-group rg-aipolicy --name redis-aipolicy --query "primaryKey" -o tsv)

# Update the Container App env var with the full connection string
az containerapp update \
  --resource-group rg-aipolicy \
  --name ca-aipolicy \
  --set-env-vars "ConnectionStrings__redis=redis-aipolicy.redis.cache.windows.net:6380,password=$REDIS_KEY,ssl=True,abortConnect=False"
```

### 6. Post-Deploy: APIM Configuration

#### 6a. Set Named Values

```bash
APIM_NAME=apim-aipolicy
TENANT_ID=$(az account show --query tenantId -o tsv)
CONTAINER_APP_URL="https://$(az containerapp show --resource-group rg-aipolicy --name ca-aipolicy --query 'properties.configuration.ingress.fqdn' -o tsv)"

az apim nv create --resource-group rg-aipolicy --service-name $APIM_NAME \
  --named-value-id EntraTenantId --display-name EntraTenantId --value $TENANT_ID

az apim nv create --resource-group rg-aipolicy --service-name $APIM_NAME \
  --named-value-id ExpectedAudience --display-name ExpectedAudience --value "api://<gateway-app-id>"

az apim nv create --resource-group rg-aipolicy --service-name $APIM_NAME \
  --named-value-id ContainerAppAudience --display-name ContainerAppAudience --value "api://<api-app-id>"

az apim nv create --resource-group rg-aipolicy --service-name $APIM_NAME \
  --named-value-id ContainerAppUrl --display-name ContainerAppUrl --value $CONTAINER_APP_URL
```

#### 6b. Disable Subscription Required

```bash
# The API uses Entra JWT auth, not subscription keys
az apim api update --resource-group rg-aipolicy --service-name $APIM_NAME \
  --api-id azure-openai-api --subscription-required false
```

#### 6c. Fix API Path

Ensure the API path is `openai` (not `openapi`):

```bash
az apim api update --resource-group rg-aipolicy --service-name $APIM_NAME \
  --api-id azure-openai-api --path openai
```

#### 6d. Set Backend URL

The backend URL should point to your Azure AI Services endpoint:

```bash
# The Terraform gateway module configures this, but verify:
az apim backend show --resource-group rg-aipolicy --service-name $APIM_NAME --backend-id openAiBackend
```

#### 6e. Upload APIM Policy

```bash
az apim api policy create --resource-group rg-aipolicy --service-name $APIM_NAME \
  --api-id azure-openai-api \
  --policy-file policies/entra-jwt-policy.xml \
  --policy-format xml
```

#### 6f. Assign Cognitive Services User Role to APIM

APIM uses managed identity to call Azure OpenAI. The Terraform `gateway` module assigns this role, but verify:

```bash
APIM_PRINCIPAL_ID=$(az apim show --resource-group rg-aipolicy --name $APIM_NAME --query identity.principalId -o tsv)

az role assignment create \
  --assignee $APIM_PRINCIPAL_ID \
  --role "Cognitive Services User" \
  --scope "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/rg-aipolicy"
```

### 7. Post-Deploy: Set SPA Redirect URIs

Now that you have the Container App URL, update the Entra app:

```bash
CONTAINER_APP_FQDN=$(az containerapp show --resource-group rg-aipolicy --name ca-aipolicy --query 'properties.configuration.ingress.fqdn' -o tsv)

az ad app update --id $CLIENT_APP_ID \
  --spa-redirect-uris "https://$CONTAINER_APP_FQDN"
```

### 8. Create Plans and Assign Clients

Use the API or dashboard to create billing plans and assign clients:

```bash
# Create a plan
curl -X POST "$CONTAINER_APP_URL/api/plans" \
  -H "Content-Type: application/json" \
  -d '{"name":"Standard","monthlyQuota":1000000,"rateLimit":60,"allowOverbilling":false}'

# Assign a client to a plan
curl -X PUT "$CONTAINER_APP_URL/api/clients" \
  -H "Content-Type: application/json" \
  -d '{"clientAppId":"<client-app-id>","planName":"Standard","displayName":"My Client App"}'
```

### 9. Verify

```bash
# Test the Container App directly
curl "$CONTAINER_APP_URL/api/usage"

# Test through APIM with a token
TOKEN=$(az account get-access-token --resource api://<gateway-app-id> --query accessToken -o tsv)
curl -X POST "https://$APIM_NAME.azure-api.net/openai/deployments/gpt-4o/chat/completions" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}'

# Configure DemoClient secrets
dotnet user-secrets --project DemoClient init
dotnet user-secrets --project DemoClient set "DemoClient:TenantId" "<tenant-id>"
dotnet user-secrets --project DemoClient set "DemoClient:ApiScope" "api://<gateway-app-id>/.default"
dotnet user-secrets --project DemoClient set "DemoClient:ApimBase" "https://$APIM_NAME.azure-api.net"
dotnet user-secrets --project DemoClient set "DemoClient:ApiVersion" "2024-02-01"
dotnet user-secrets --project DemoClient set "DemoClient:AIPolicyBase" "$CONTAINER_APP_URL"
dotnet user-secrets --project DemoClient set "DemoClient:Clients:0:Name" "Demo Client"
dotnet user-secrets --project DemoClient set "DemoClient:Clients:0:AppId" "<client-app-id>"
dotnet user-secrets --project DemoClient set "DemoClient:Clients:0:Secret" "<client-secret>"
dotnet user-secrets --project DemoClient set "DemoClient:Clients:0:Plan" "Standard"
dotnet user-secrets --project DemoClient set "DemoClient:Clients:0:DeploymentId" "gpt-4o"

# setup-azure.ps1 also generates demo/.env.local with DemoClient__* values.
# For a manual template, copy demo/.env.sample.
# DemoClient automatically loads .env.local/.env when present.
# setup-azure.ps1 also generates src/aipolicy-ui/.env.production.local so the deployed dashboard uses the current Entra app IDs.

# Run the DemoClient (Agent Framework 1.0.0-rc2) for synthetic traffic
cd src
dotnet run demo/DemoClient.cs
```

---

## Terraform Variables Reference

Input variables for `infra/terraform/variables.tf` (see `infra/terraform/terraform.tfvars.sample` for example values):

| Variable | Required | Description |
|----------|----------|-------------|
| `workload_name` | Yes | Short name used to generate unique resource names |
| `location` | Yes | Azure region (e.g. `eastus2`) |
| `subscription_id` | Yes | Azure subscription ID |
| `tenant_id` | Yes | Entra tenant ID |
| `entra_api_app_id` | Yes | App registration for the Container App audience |
| `entra_gateway_app_id` | Yes | App registration for APIM JWT audience |
| `purview_client_app_id` | No | Entra app ID for Purview (leave empty to skip) |

---

## APIM Policy Explanation

The `policies/entra-jwt-policy.xml` policy handles the full request lifecycle:

### Inbound
1. **JWT Validation** — Validates the `Authorization: Bearer` token against your Entra tenant's OpenID configuration. Requires the `aud` claim to match `{{ExpectedAudience}}`.
2. **Claim Extraction** — Extracts `tid`, `azp`/`appid`, and `aud` from the JWT into APIM context variables.
3. **Pre-check Call** — Sends a `GET /api/precheck/{clientAppId}` request to the Container App. If the client has no plan (401), exceeded quota (429), or other error, the request is rejected before reaching OpenAI.
4. **Backend + Managed Identity** — Routes to the Azure OpenAI backend using APIM's managed identity (`authentication-managed-identity` for `https://cognitiveservices.azure.com/`).
5. **Stream Options** — If the request has `"stream": true`, injects `"stream_options": {"include_usage": true}` so the final chunk includes token counts.

### Outbound
1. **Response Capture** — Captures the full response body as text.
2. **Response Parsing** — For streaming responses, extracts the last `data:` chunk containing usage info. For non-streaming, parses the full JSON.
3. **Fire-and-Forget Log** — Sends a one-way POST to `{{ContainerAppUrl}}/api/log` with the request body, parsed response, JWT claims, and deployment ID. This does not add latency to the response.

### `azp` vs `appid` Fallback

```csharp
// azp is present in delegated (user) tokens, appid in client_credentials tokens
var azp = jwt?.Claims.GetValueOrDefault("azp","");
return !string.IsNullOrEmpty(azp) ? azp : jwt?.Claims.GetValueOrDefault("appid","");
```

---

## Environment Variables Reference

Variables configured on the Container App by the Terraform `compute` module:

| Variable | Required | Description |
|----------|----------|-------------|
| `ASPNETCORE_URLS` | Auto | Set to `http://+:8080` by Terraform |
| `ConnectionStrings__redis` | Yes | Redis connection string |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | App Insights telemetry export |
| `PURVIEW_CLIENT_APP_ID` | No | Entra app ID for Purview API |
| `AZURE_SUBSCRIPTION_ID` | Auto | Set by Terraform from deployment context |
| `AZURE_RESOURCE_GROUP` | Auto | Set by Terraform from deployment context |
| `REDIS_NAME` | Auto | Redis cache name (set by Terraform) |

Additional Purview variables (set manually if using Purview):

| Variable | Description |
|----------|-------------|
| `PURVIEW_TENANT_ID` | Tenant ID (auto-detected from token if not set) |
| `PURVIEW_APP_NAME` | App name shown in Purview audit (default: "AI Policy API") |
| `PURVIEW_APP_LOCATION` | App URL for Purview policy location |
| `PURVIEW_IGNORE_EXCEPTIONS` | If true, Purview errors are logged but not thrown |
| `PURVIEW_BACKGROUND_JOB_LIMIT` | Max queued background audit jobs (default: 100) |
| `PURVIEW_MAX_CONCURRENT_CONSUMERS` | Max concurrent audit workers (default: 10) |

---

## KQL Queries

Custom OpenTelemetry metrics are queryable in Application Insights:

```kusto
// Token usage by tenant
customMetrics
| where name == "aipolicy.tokens_processed"
| extend tenant_id = tostring(customDimensions.tenant_id)
| summarize total_tokens = sum(value) by tenant_id, bin(timestamp, 1h)

// Cost distribution by model
customMetrics
| where name == "aipolicy.cost_total"
| extend model = tostring(customDimensions.model)
| summarize avg_cost = avg(value), total_cost = sum(value) by model

// Request volume by client app
customMetrics
| where name == "aipolicy.requests_processed"
| extend client_app = tostring(customDimensions.client_app_id)
| summarize request_count = sum(value) by client_app, bin(timestamp, 1h)
```

---

## Troubleshooting

### AADSTS9002326: Cross-origin token redemption is permitted only for the 'Single-Page Application' client type

**Cause**: The SPA redirect URIs are not configured on the Entra app registration.

**Fix**: Add the Container App URL as a SPA redirect URI:
```bash
az ad app update --id $CLIENT_APP_ID \
  --spa-redirect-uris "https://<container-app-fqdn>"
```

### APIM 500 with "No such host is known"

**Cause**: The `ContainerAppUrl` named value in APIM is incorrect or missing.

**Fix**: Verify and update the named value:
```bash
az apim nv update --resource-group rg-aipolicy --service-name $APIM_NAME \
  --named-value-id ContainerAppUrl --value "https://<correct-container-app-fqdn>"
```

### APIM 500 with BackendConnectionFailure

**Cause**: APIM's managed identity doesn't have the `Cognitive Services User` role on the Azure AI Services resource.

**Fix**: Assign the role:
```bash
az role assignment create \
  --assignee $APIM_PRINCIPAL_ID \
  --role "Cognitive Services User" \
  --scope "/subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<ai-service-name>"
```

### Tokens Not Tracking (usage shows 0)

**Cause**: The `azp`/`appid` claim extraction in the APIM policy isn't matching the client. Or the Container App's `/api/log` endpoint isn't reachable from APIM.

**Fix**:
1. Decode the JWT at [jwt.ms](https://jwt.ms) and verify the `azp` or `appid` claim is present
2. Test the Container App directly: `curl https://<container-app-url>/api/usage`
3. Check APIM trace logs for errors in the outbound `send-one-way-request`

### Dashboard White Screen After Login

**Cause**: MSAL is not initialized or the Entra app configuration is wrong.

**Fix**: Check that the `msalReady` state is being set in the React app. Verify the client ID and tenant ID in the frontend MSAL configuration match the Entra app registration.
