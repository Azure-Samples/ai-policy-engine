# Frequently Asked Questions

## General Questions

### What is this solution?
An enterprise-ready chargeback and usage-tracking layer that sits between your applications and Azure OpenAI. Built on ASP.NET Minimal API (.NET 10), Azure Container Apps, Azure API Management, and Redis — it provides quota enforcement, rate limiting, per-client billing, and a React dashboard for visibility.

### What problem does it solve?
- **Cost Tracking**: No native way to track and allocate Azure OpenAI costs across teams, applications, or departments.
- **Quota & Rate Enforcement**: Gate requests *before* they reach OpenAI, preventing runaway usage.
- **Data Governance**: Minimal data persistence in Redis with configurable TTLs, plus optional Purview audit integration.

---

## Technical Questions

### What's the max payload size?
**Container App limit** — default 100 MB.

### How long is data retained?

| Data Type | TTL |
|-----------|-----|
| Usage logs (`{tenantId}-{clientAppId}-{deploymentId}`) | 24 hours |
| Plans (`plan:{id}`) | No TTL (persistent) |
| Clients (`client:{appId}`) | No TTL (persistent) |
| Pricing (`pricing:{modelId}`) | No TTL (persistent) |
| Traces (`traces:{clientAppId}`) | 7 days |
| Rate limit counters | 2 minutes |
| Azure Monitor / Application Insights | Per workspace retention settings |

### Multi-region support?
Yes. Both Azure Container Apps and Azure API Management support multi-region deployment. The Bicep modules in `infra/` and `infra/` can be parameterized for additional regions.

### Do I need subscription keys?
**No.** All authentication uses Entra ID JWT bearer tokens exclusively. Subscription key requirements are disabled on all APIM APIs.

### Do I need a Purview / E5 license?
Only for the **optional** Microsoft Purview DLP/audit integration. The core billing, chargeback, quota enforcement, and dashboard functionality works without it.

### Can I still use the Python implementation?
The original Python code is preserved in `src/`, `app/backend/`, and `app/frontend/` for reference. The **.NET 10 implementation** (in `src/`) is the actively maintained version.

---

## Rate Limiting & Billing

### How does rate limiting work?
- **RPM (requests per minute)** is checked during the **precheck** (inbound, *before* the OpenAI call). If the client exceeds their plan's RPM limit, a `429 Too Many Requests` is returned.
- **TPM (tokens per minute)** is updated during the **log** (outbound, *after* the response with the actual token count).
- Redis sliding window with **2-minute bucket expiry** ensures counters auto-clean.

### How does overbilling work?
When a plan has `allowOverbilling = true`, requests that exceed the monthly token quota are **still allowed** but marked as overbilled. Customer cost is calculated using the plan's `costPerMillionTokens` rate, applied only to the overbilled portion.

### How do I add a new model?
Navigate to the **Pricing** page in the React dashboard and add a new model with its input/output rates. The billing calculator picks up changes within 30 seconds (next Redis read).

---

## Authentication & Identity

### What JWT claim is used for client identity?
- **`appid`** — for the `client_credentials` flow (service-to-service).
- **`azp`** — for delegated/on-behalf-of flow (user-interactive).

The APIM policy checks both claims with fallback logic, so both grant types work transparently.

### How does APIM authenticate to Azure OpenAI?
APIM uses its **system-assigned managed identity** to obtain a token for the Azure OpenAI resource. No API keys or secrets are involved.

---

## Deployment

### How do I deploy from scratch?
Run the setup script:
```powershell
./scripts/setup-azure.ps1
```
Or follow the step-by-step guide in [`docs/DOTNET_DEPLOYMENT_GUIDE.md`](./DOTNET_DEPLOYMENT_GUIDE.md).

### What Azure resources are required?
- Azure Container Apps environment + Container App
- Azure API Management instance
- Azure Cache for Redis
- Azure OpenAI resource
- Entra ID app registrations (for JWT validation)
- Azure Monitor / Application Insights workspace
- (Optional) Microsoft Purview account

### What IaC tool is used?
**Bicep** exclusively. Infrastructure modules live in `infra/` (AI services) and `infra/` (API Management).

---

## Troubleshooting

### Common deployment issues?
1. **Entra app registration** — ensure the app registration has the correct `api://` identifier URI and the APIM `validate-jwt` policy references the right audience.
2. **Managed identity** — the APIM managed identity must have the `Cognitive Services OpenAI User` role on the Azure OpenAI resource.
3. **Redis connectivity** — verify the Container App has network access to the Redis instance and the connection string is correct.
4. **Container image** — ensure the image is built for `linux/amd64` and pushed to the correct container registry.

### How do I debug API issues?
1. Check **Azure Monitor / Application Insights** for distributed traces.
2. Review the Container App **console logs** for startup or runtime errors.
3. Test endpoints directly: `GET /api/plans`, `GET /api/clients` to verify Redis connectivity.
4. Check APIM **trace** mode (enable in the Azure portal) to see policy execution details.
5. Verify the Redis data: use the dashboard or `redis-cli` to inspect keys.

---

## Getting Help

- **📖 Documentation**: Guides in `/docs/`
- **🐛 Issues**: [GitHub Issues](https://github.com/your-org/repo/issues)
- **💬 Discussions**: [GitHub Discussions](https://github.com/your-org/repo/discussions)
