# Skill: azd + Terraform Large Deployment Pattern

## Problem

When deploying 70+ Azure resources via `azd up` with Terraform, the deployment can take 10-60+ minutes. Without proper monitoring, it's unclear whether the deployment is progressing, stalled, or failed. Blocking on the command prevents other work and risks timeouts.

## Solution

Use async PowerShell execution with periodic polling to monitor long-running deployments non-blockingly.

### Deployment Pattern

```powershell
# Launch deployment in async mode with 2-minute initial wait
azd up --no-prompt
# mode: async, initial_wait: 120, shellId: azd-up-deployment

# Poll periodically (every 5 minutes) to check progress
read_powershell shellId: azd-up-deployment, delay: 300

# Continue until exit code 0 (success) or non-zero (failure)
```

### Key Decisions

1. **Async Mode:** Prevents shell blocking on long-running commands. Allows periodic status checks.
2. **Initial Wait:** 120 seconds to capture startup messages (module upgrades, provider initialization).
3. **Poll Interval:** 300 seconds (5 minutes) balances responsiveness with API overhead.
4. **No-Prompt Flag:** `--no-prompt` prevents interactive confirmation prompts that would block async execution.

### Timing Expectations (77 Resources Example)

From real deployment (ai-policy-engine-k8m2):
- **Total Duration:** ~10 minutes (77 resources)
  - Provisioning: 9 minutes 8 seconds
  - Deploying: 50 seconds
- **Longest Pole:** Redis Enterprise cluster (6m22s)
- **APIM Service:** 3-4 minutes
- **Container App:** 18 seconds (after Redis + Cosmos ready)
- **Role Assignments:** 20-25 seconds per assignment

### Resource Creation Order (Terraform Dependency Graph)

1. **Foundation (0-2 min):** Resource Group, Key Vault, Log Analytics, App Insights, Managed Identity
2. **Data Layer (2-9 min):** Redis Enterprise (longest pole), Cosmos DB
3. **Compute Layer (7-9 min):** Container Apps Environment, Aspire Dashboard, APIM Service
4. **AI Services (4-6 min):** Cognitive Services, AI Services deployment
5. **Gateway Layer (9-10 min):** APIM APIs, Operations, Policies (depends on Container App URL)
6. **Access Control (9-10 min):** Role Assignments, Redis access policies (parallel)

### Gotchas

- **Redis Enterprise Creation:** Budget 7-10 minutes. This is always the longest resource.
- **APIM Policy Timing:** Policies applied AFTER Container App URL is available (named value dependency). Ensure Container App is created first.
- **Parallel Provisioning:** azd overlaps packaging with provisioning. API container image builds in parallel with Terraform apply. Efficient by default.
- **Exit Code 0 = Success:** Only trust exit code. Partial output may look like failure but command is still running.

### Validation After Deployment

1. **Check Container App Health:**
   ```powershell
   $url = "https://<container-app-url>/health"
   Invoke-WebRequest -Uri $url -Method Get
   # Expected: 200 OK, "Healthy"
   ```

2. **Check APIM Gateway:**
   ```powershell
   $url = "https://<apim-gateway-url>"
   Invoke-WebRequest -Uri $url -Method Get
   # Expected: 401/403/404 (gateway enforces auth)
   ```

3. **Verify Terraform Outputs:**
   - Container App URL
   - APIM Gateway URL
   - Cosmos Endpoint
   - Redis Hostname
   - Key Vault Name
   - Resource Group Name

### When to Retry

- **Transient Failures:** Throttling (429), timeouts (408), service unavailable (503) → Retry once after 5 minutes
- **Real Config Issues:** Invalid Terraform syntax, RBAC denials, quota limits, policy violations → Fix config, validate with `azd provision --preview`, then retry
- **Auth Errors:** AADSTS codes → Check auth alignment (see azd-terraform-auth-alignment skill)

### Files to Commit After Success

Per team decision (2026-05-14T15:54:00Z — "Always validate infra fixes before committing"):
- Commit `azure.yaml` and `infra/terraform/*.tfvars.json` ONLY after successful `azd up`
- DO NOT commit speculative/unvalidated fixes
- Keeps commit tree clean of bad history

## Key Learning

For large Terraform deployments (50+ resources):
- Always use async mode + periodic polling
- Budget 2× the expected provisioning time for safety margin
- Redis Enterprise is always the longest pole in data layer
- Validate endpoints after deployment completes
- Only commit infra files after successful validation

## When to Apply

Use this pattern when:
- Deploying 50+ Azure resources
- Using azd + Terraform provider
- Deployment expected to take 10+ minutes
- Need to monitor progress without blocking
- Want to continue other work during deployment
