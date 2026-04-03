# Usage Examples

This document provides examples for calling AI models through the Azure AI Gateway Policy Engine. All examples use **Microsoft Entra ID (Azure AD) Bearer tokens** for authentication.

> **Note:** Replace placeholder values (`YOUR-TENANT-ID`, `YOUR-CLIENT-APP-ID`, `YOUR-CLIENT-SECRET`, `YOUR-API-APP-ID`, `your-apim`) with your actual configuration.

## Authentication

All requests to the APIM gateway require a valid Entra Bearer token. There are two common flows:

- **Delegated (interactive):** Use `az account get-access-token` or MSAL interactive flows.
- **Client credentials (service-to-service):** Use MSAL `ConfidentialClientApplication` or `ClientSecretCredential`.

---

## cURL

```bash
# Get a token using Azure CLI (delegated flow)
TOKEN=$(az account get-access-token --resource api://YOUR-API-APP-ID --query accessToken -o tsv)

# Call Azure OpenAI through APIM
curl -X POST "https://your-apim.azure-api.net/openai/deployments/gpt-4o/chat/completions?api-version=2024-02-01" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"messages": [{"role": "user", "content": "Hello!"}], "max_tokens": 100}'
```

---

## Python (MSAL + requests)

```python
import requests
from msal import ConfidentialClientApplication

# Client credentials flow (service-to-service)
app = ConfidentialClientApplication(
    client_id="YOUR-CLIENT-APP-ID",
    client_credential="YOUR-CLIENT-SECRET",
    authority="https://login.microsoftonline.com/YOUR-TENANT-ID"
)

result = app.acquire_token_for_client(scopes=["api://YOUR-API-APP-ID/.default"])
token = result["access_token"]

response = requests.post(
    "https://your-apim.azure-api.net/openai/deployments/gpt-4o/chat/completions",
    params={"api-version": "2024-02-01"},
    headers={
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    },
    json={
        "messages": [{"role": "user", "content": "Hello!"}],
        "max_tokens": 100
    }
)
print(response.json())
```

---

## Python (Azure Identity + OpenAI SDK)

```python
from azure.identity import ClientSecretCredential
from openai import AzureOpenAI

credential = ClientSecretCredential(
    tenant_id="YOUR-TENANT-ID",
    client_id="YOUR-CLIENT-APP-ID",
    client_secret="YOUR-CLIENT-SECRET"
)

# Use APIM as the base URL (not the Azure OpenAI endpoint directly)
client = AzureOpenAI(
    azure_endpoint="https://your-apim.azure-api.net",
    azure_ad_token_provider=lambda: credential.get_token("api://YOUR-API-APP-ID/.default").token,
    api_version="2024-02-01"
)

response = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Hello!"}],
    max_tokens=100
)
print(response.choices[0].message.content)
```

---

## C# / .NET

```csharp
using Azure.Identity;
using Azure.AI.OpenAI;

var credential = new ClientSecretCredential(
    "YOUR-TENANT-ID", "YOUR-CLIENT-APP-ID", "YOUR-CLIENT-SECRET");

var client = new AzureOpenAIClient(
    new Uri("https://your-apim.azure-api.net"),
    credential);

var chatClient = client.GetChatClient("gpt-4o");
var response = await chatClient.CompleteChatAsync("Hello!");
Console.WriteLine(response.Value.Content[0].Text);
```

---

## JavaScript / TypeScript

```typescript
import { ConfidentialClientApplication } from "@azure/msal-node";

const msalApp = new ConfidentialClientApplication({
    auth: {
        clientId: "YOUR-CLIENT-APP-ID",
        clientSecret: "YOUR-CLIENT-SECRET",
        authority: "https://login.microsoftonline.com/YOUR-TENANT-ID"
    }
});

const result = await msalApp.acquireTokenByClientCredential({
    scopes: ["api://YOUR-API-APP-ID/.default"]
});

const response = await fetch(
    "https://your-apim.azure-api.net/openai/deployments/gpt-4o/chat/completions?api-version=2024-02-01",
    {
        method: "POST",
        headers: {
            "Authorization": `Bearer ${result!.accessToken}`,
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            messages: [{ role: "user", content: "Hello!" }],
            max_tokens: 100
        })
    }
);
console.log(await response.json());
```

---

## Dashboard API Examples

The chargeback dashboard API uses the same Entra Bearer token authentication.

```bash
TOKEN=$(az account get-access-token --resource api://YOUR-API-APP-ID --query accessToken -o tsv)
API_BASE="https://your-apim.azure-api.net"
```

### Create a Plan

```bash
curl -X POST "$API_BASE/api/plans" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "Engineering Team", "monthlyBudget": 5000.00, "costPerToken": 0.00003}'
```

### Assign a Client to a Plan

```bash
curl -X PUT "$API_BASE/api/clients/YOUR-CLIENT-APP-ID" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"planId": "plan-id-here", "displayName": "My Service App"}'
```

### Check Client Usage

```bash
curl -s "$API_BASE/api/clients/YOUR-CLIENT-APP-ID/usage" \
  -H "Authorization: Bearer $TOKEN" | jq .
```

### Export Usage as CSV

```bash
curl -s "$API_BASE/api/export/csv?startDate=2024-01-01&endDate=2024-01-31" \
  -H "Authorization: Bearer $TOKEN" -o usage-report.csv
```
