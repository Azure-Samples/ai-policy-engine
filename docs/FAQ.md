# Frequently Asked Questions

## General Questions

### What is Azure AI Gateway Policy Engine?
An enterprise-ready policy engine that sits between your applications and AI models (Azure OpenAI, Foundry, etc.) via Azure API Management. Built on ASP.NET Minimal API (.NET 10), Azure Container Apps, and inspired by telecom/RADIUS principles. It provides:
- **Authentication & Authorization** at the API gateway (pre-checks before the backend)
- **Intelligent Model Routing** (auto-router to optimal deployments based on plan policies)
- **Per-Request Multiplier Pricing** (GHCP-style billing, not just tokens)
- **Quota & Rate Enforcement** (monthly limits and request/token rate limits)
- **Durable Configuration** (CosmosDB source of truth, Redis cache)
- **Bill-Back Reporting** (per-client consumption tracking and CSV exports)
- **React Dashboard** for real-time visibility across all customers

### What problem does it solve?
- **Cost Accountability**: No native way to track and allocate AI API costs across teams, applications, or departments.
- **Quota & Rate Enforcement**: Gate requests *before* they reach the backend, preventing runaway usage and surprise bills.
- **Configuration Durability**: Store billing plans, customer assignments, pricing, and routing policies durably (not just in Redis).
- **Multi-Tenant Billing**: SaaS apps serving multiple customers — each customer gets independent quotas, rate limits, and billing.
- **Flexible Pricing Models**: Support both token-based and per-request multiplier pricing in the same system.
- **Intelligent Routing**: Route requests to optimal deployments based on customer plan and availability.

---

## Technical Questions

### What's the max payload size?
**Container App limit** — default 100 MB.

### How long is data retained?

| Data Type | Storage | Retention |
|-----------|---------|-----------|
| Configuration (plans, clients, pricing, routing policies) | CosmosDB | Indefinite (durable source of truth) |
| Configuration (live) | Redis | Write-through cache; persists to CosmosDB |
| Usage logs | CosmosDB | 36 months (for billing records) |
| Audit trail | CosmosDB | 36 months (for compliance) |
| Rate limit counters | Redis | 2 minutes (window-based) |
| Azure Monitor / Application Insights | Log Analytics | Per workspace retention settings |

### Multi-region support?
Yes. Both Azure Container Apps and Azure API Management support multi-region deployment. The Bicep modules in `infra/` can be parameterized for additional regions.

### Do I need subscription keys?
**No.** All authentication uses Entra ID JWT bearer tokens exclusively. Subscription key requirements are disabled on all APIM APIs.

### Do I need a Purview / E5 license?
Only for the **optional** Microsoft Purview DLP/audit integration. The core policy engine (authentication, authorization, routing, billing, dashboard) works without it.

---

## Billing & Pricing

### What's multiplier pricing?
Instead of per-token billing, multiplier pricing charges per-request with a configurable model tier multiplier. Example:
- **GPT-4.1** = 1.0x baseline → every request = 1 effective request against quota
- **GPT-4.1-mini** = 0.33x → every 3 requests = 1 effective request against quota
- **GPT-35-turbo** = 0.1x → every 10 requests = 1 effective request against quota

Perfect for flat-rate or per-seat consumption models (like GHCP billing).

### Can I use both token-based and multiplier-based pricing?
**Yes.** Each plan independently specifies its billing model:
- Some customers use token-based billing (pay per 1K tokens)
- Others use multiplier-based billing (pay per request × model tier)
- The dashboard adapts to show both modes side-by-side (hybrid mode)

### How are quotas enforced?
Two mechanisms:
1. **Per-customer monthly quota**: Each customer (clientAppId:tenantId) gets a monthly allowance (either tokens or effective requests, depending on plan).
2. **Rate limits**: RPM (requests/min) and TPM (tokens/min) enforce immediate rate limiting before the backend.

All checks happen at APIM precheck time — before the backend request is made. Exceeding quotas → 429 Too Many Requests.

---

## Architecture & Storage

### Is all configuration stored durably?
**Yes.** CosmosDB is the **source of truth** for:
- Billing plans (with pricing, quotas, rate limits, multipliers)
- Customer assignments (client app + tenant → plan mapping)
- Routing policies (model routing rules per customer)
- Usage policies (deployment access controls)

Redis serves as a **write-through cache** for performance. On startup, all configuration is automatically loaded from CosmosDB into Redis. You can safely upgrade or restart — nothing is lost.

### How does model routing work?
The **auto-router** evaluates routing policies per customer and selects the best deployment based on:
- Plan routing policy (per-account, enforced, or QoS-based)
- Availability (from model discovery service)
- Quotas and rate limits on each candidate deployment

It **doesn't rewrite what the client requested** — if they ask for GPT-4, they still get GPT-4 (or a configured fallback if unavailable). Enforced rewriting (e.g., redirect GPT-4 → GPT-4o-mini) is a future policy engine feature.

### What if I have multiple AI model providers?
The system is provider-agnostic. Model discovery can pull from:
- Azure OpenAI service
- Azure Foundry deployments
- Custom discovery services (implement `IDeploymentDiscoveryService`)

All pricing, routing, and quota logic works the same regardless of provider.

---

## Rate Limiting & Quotas

### How does rate limiting work?
- **RPM (requests per minute)** is checked during **precheck** (inbound, *before* the backend call). If the client exceeds their plan's RPM limit, a `429 Too Many Requests` is returned.
- **TPM (tokens per minute)** is updated during the **log** (outbound, *after* the response with the actual token count).
- Rate limits apply to the **routed deployment** (the one that actually processes the request), not the originally requested model.

### What happens when a customer exceeds their monthly quota?
If a customer exceeds their monthly quota (effective requests or tokens, depending on plan):
- New requests are rejected at APIM precheck with **429 Too Many Requests**
- If the plan has overbilling enabled, requests may still be forwarded but flagged as "overage"
- Overage costs are tracked separately for bill-back reporting

---

## Multi-Tenancy

### How does multi-tenant billing work?
A **"customer"** in the billing system is uniquely identified by `clientAppId` (Entra app) + `tenantId` (Entra directory), not just the app alone.

**Scenario 1**: SaaS company with one app registered as `AzureADMultipleOrgs`
- Can authenticate users from any Entra tenant
- Each unique `tenantId` gets a separate customer entry with its own plan, quota, rate limits, and billing
- Perfect for selling the same app to multiple organizations

**Scenario 2**: Internal platform team with a shared client app
- One app (`AzureADSingleOrg`) used by multiple departments
- Each department's tenant gets independent billing
- Finance can report costs per department even though they share one app registration

### Can I pre-register customers for multiple tenants?
**Yes.** During deployment, use the `-SecondaryTenantId` flag:
```powershell
./scripts/setup-azure.ps1 -SecondaryTenantId "<other-tenant-guid>"
```

Or register customers manually via API after deployment.

---

## Deployment & Infrastructure

### Should I use Terraform or Bicep?
Both paths deploy the **identical infrastructure**:

| Aspect | Bicep | Terraform |
|--------|-------|-----------|
| **Simplicity** | One-command `setup-azure.ps1` | Two-stage deploy (infra, then container) |
| **State Management** | Implicit (Azure deployment history) | Explicit (tfstate file) |
| **Team Preference** | Smaller teams, quick start | Teams already using Terraform |
| **Customization** | Straightforward parameter edits | Better for complex variable hierarchies |

**Recommendation**: Start with Bicep for simplicity. If you already have Terraform infrastructure, use the Terraform path for consistency.

### What if the deployment script fails?
1. Check the error message — most failures are permission-related (Contributor role, Entra admin)
2. Verify Azure CLI is authenticated: `az account show`
3. For Entra permission issues, ask your Azure AD admin to grant Application Administrator role
4. See the [Deployment Guide](DOTNET_DEPLOYMENT_GUIDE.md) for manual step-by-step instructions

### Can I deploy to multiple regions?
**Yes.** The Bicep and Terraform modules support multi-region deployment. Parameters allow you to:
- Specify region for each resource group
- Configure region-specific APIM instances
- Set up cross-region failover with APIM backends

See `infra/bicep/main.bicep` and `infra/terraform/variables.tf` for region parameters.

---

## Troubleshooting

### My customers are getting 429 (Too Many Requests)
Possible causes:
1. **Monthly quota exceeded**: Check customer's quota and usage in the dashboard
2. **Rate limit exceeded**: Client sent too many requests in the last minute (RPM/TPM limit)
3. **Deployment routed to quota**: The routed deployment has hit its own quota

**Fix**: Adjust plan quotas/rate limits in the Plans page, or check model routing logic.

### Precheck is slow
Precheck latency should be <10ms. If slower:
1. **Cache miss**: Redis might be evicting entries. Check Redis memory usage.
2. **CosmosDB latency**: Check CosmosDB request units (RU) consumption.
3. **Network**: Check network latency between Container App and Redis/CosmosDB.

**Mitigation**: Warm up the cache on startup (`CacheWarmingService`), or increase Redis tier.

### Why is my bill-back report missing data?
Bill-back reports are built from audit logs stored in CosmosDB. If data is missing:
1. **No logs recorded**: Check if `/api/log` endpoint is being called by APIM
2. **Logs recorded but not exported**: Check export date range — default is current month
3. **Role missing**: Export endpoints require `AIPolicy.Export` app role

**Debug**: Check Azure Monitor logs for `/api/log` POST failures.

---

## More Questions?
Check the [Architecture Documentation](ARCHITECTURE.md) for system design details, or [Usage Examples](USAGE_EXAMPLES.md) for code samples.
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
