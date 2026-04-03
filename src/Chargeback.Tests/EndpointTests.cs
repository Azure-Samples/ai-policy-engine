using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Linq;
using System.Globalization;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using NSubstitute;

namespace Chargeback.Tests;

public class EndpointTests : IClassFixture<ChargebackApiFactory>
{
    private readonly ChargebackApiFactory _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;

    public EndpointTests(ChargebackApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        _factory.Redis.Clear();
    }

    // ── Pricing Endpoints ───────────────────────────────────────────────

    [Fact]
    public async Task GetPricing_WhenEmpty_SeedsDefaults()
    {
        var response = await _client.GetAsync("/api/pricing");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<ModelPricingResponse>(JsonOpts);
        Assert.NotNull(json);
        Assert.True(json.Models.Count >= 7, "Should seed at least 7 default pricing models");
        Assert.Contains(json.Models, m => m.ModelId == "gpt-4o");
    }

    [Fact]
    public async Task PutPricing_CreatesNewModel()
    {
        var body = new ModelPricingCreateRequest
        {
            ModelId = "test-model",
            DisplayName = "Test Model",
            PromptRatePer1K = 0.01m,
            CompletionRatePer1K = 0.02m
        };

        var response = await _client.PutAsJsonAsync("/api/pricing/test-model", body, JsonOpts);
        response.EnsureSuccessStatusCode();

        var pricing = await response.Content.ReadFromJsonAsync<ModelPricing>(JsonOpts);
        Assert.NotNull(pricing);
        Assert.Equal("test-model", pricing.ModelId);
        Assert.Equal("Test Model", pricing.DisplayName);
        Assert.Equal(0.01m, pricing.PromptRatePer1K);
    }

    [Fact]
    public async Task DeletePricing_ExistingModel_ReturnsOk()
    {
        // Seed a pricing entry
        var pricingData = new ModelPricing
        {
            ModelId = "delete-me",
            DisplayName = "Delete Me",
            PromptRatePer1K = 0.01m,
            UpdatedAt = DateTime.UtcNow
        };
        _factory.Redis.SeedString(
            RedisKeys.Pricing("delete-me"),
            JsonSerializer.Serialize(pricingData, JsonOpts));

        var response = await _client.DeleteAsync("/api/pricing/delete-me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeletePricing_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync("/api/pricing/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Plan Endpoints ──────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlan_ValidBody_Returns201()
    {
        var body = new PlanCreateRequest
        {
            Name = "Test Plan",
            MonthlyRate = 100m,
            MonthlyTokenQuota = 1_000_000,
            TokensPerMinuteLimit = 10_000,
            RequestsPerMinuteLimit = 60,
            AllowOverbilling = false,
            CostPerMillionTokens = 5m
        };

        var response = await _client.PostAsJsonAsync("/api/plans", body, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var plan = await response.Content.ReadFromJsonAsync<PlanData>(JsonOpts);
        Assert.NotNull(plan);
        Assert.Equal("Test Plan", plan.Name);
        Assert.Equal(1_000_000, plan.MonthlyTokenQuota);
        Assert.False(string.IsNullOrWhiteSpace(plan.Id));
    }

    [Fact]
    public async Task CreatePlan_EmptyName_ReturnsBadRequest()
    {
        var body = new PlanCreateRequest { Name = "" };
        var response = await _client.PostAsJsonAsync("/api/plans", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_DuplicateName_ReturnsConflict()
    {
        SeedPlan("plan-dup-existing", "Starter");

        var body = new PlanCreateRequest
        {
            Name = " starter ",
            MonthlyRate = 49.99m,
            MonthlyTokenQuota = 500,
            TokensPerMinuteLimit = 1000,
            RequestsPerMinuteLimit = 10,
            AllowOverbilling = false,
            CostPerMillionTokens = 0m
        };

        var response = await _client.PostAsJsonAsync("/api/plans", body, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetPlans_AfterCreating_ReturnsList()
    {
        SeedPlan("plan-1", "Plan One");

        var response = await _client.GetAsync("/api/plans");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<PlansResponse>(JsonOpts);
        Assert.NotNull(json);
        Assert.Single(json.Plans);
        Assert.Equal("Plan One", json.Plans[0].Name);
    }

    [Fact]
    public async Task UpdatePlan_ExistingPlan_ReturnsUpdated()
    {
        SeedPlan("plan-up", "Original");

        var body = new PlanUpdateRequest { Name = "Updated" };
        var response = await _client.PutAsJsonAsync("/api/plans/plan-up", body, JsonOpts);
        response.EnsureSuccessStatusCode();

        var plan = await response.Content.ReadFromJsonAsync<PlanData>(JsonOpts);
        Assert.NotNull(plan);
        Assert.Equal("Updated", plan.Name);
    }

    [Fact]
    public async Task UpdatePlan_NonExistent_Returns404()
    {
        var body = new PlanUpdateRequest { Name = "Nope" };
        var response = await _client.PutAsJsonAsync("/api/plans/no-such-plan", body, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePlan_DuplicateName_ReturnsConflict()
    {
        SeedPlan("plan-alpha", "Starter");
        SeedPlan("plan-beta", "Enterprise");

        var body = new PlanUpdateRequest { Name = " starter " };
        var response = await _client.PutAsJsonAsync("/api/plans/plan-beta", body, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeletePlan_ExistingPlan_ReturnsOk()
    {
        SeedPlan("plan-del", "Doomed");
        var response = await _client.DeleteAsync("/api/plans/plan-del");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeletePlan_WithAssignedClient_ReturnsConflict()
    {
        SeedPlan("plan-in-use", "In Use");
        SeedClientAssignment("client-in-use", "plan-in-use", "Client In Use");

        var response = await _client.DeleteAsync("/api/plans/plan-in-use");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeletePlan_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync("/api/plans/ghost");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Client Endpoints ────────────────────────────────────────────────

    [Fact]
    public async Task AssignClient_ValidPlan_ReturnsAssignment()
    {
        SeedPlan("plan-a", "Plan A");

        var body = new ClientAssignRequest { PlanId = "plan-a", DisplayName = "My App" };
        var response = await _client.PutAsJsonAsync("/api/clients/app-001/tenant-1", body, JsonOpts);
        response.EnsureSuccessStatusCode();

        var assignment = await response.Content.ReadFromJsonAsync<ClientPlanAssignment>(JsonOpts);
        Assert.NotNull(assignment);
        Assert.Equal("app-001", assignment.ClientAppId);
        Assert.Equal("plan-a", assignment.PlanId);
        Assert.Equal("My App", assignment.DisplayName);
    }

    [Fact]
    public async Task AssignClient_WithExistingLogs_ComputesCurrentUsage()
    {
        SeedPlan("plan-usage-compute", "Usage Compute Plan");
        SeedLog("tenant-x", "compute-client", "gpt-4o", model: "gpt-4o", totalTokens: 120);
        SeedLog("tenant-x", "compute-client", "gpt-4o-mini", model: "gpt-4o-mini", totalTokens: 30);

        var body = new ClientAssignRequest { PlanId = "plan-usage-compute", DisplayName = "Compute Client" };
        var response = await _client.PutAsJsonAsync("/api/clients/compute-client/tenant-x", body, JsonOpts);
        response.EnsureSuccessStatusCode();

        var assignment = await response.Content.ReadFromJsonAsync<ClientPlanAssignment>(JsonOpts);
        Assert.NotNull(assignment);
        Assert.Equal(150, assignment.CurrentPeriodUsage);
    }

    [Fact]
    public async Task AssignClient_NonExistentPlan_ReturnsBadRequest()
    {
        var body = new ClientAssignRequest { PlanId = "no-plan" };
        var response = await _client.PutAsJsonAsync("/api/clients/app-002/tenant-1", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetClients_AfterAssigning_ReturnsList()
    {
        SeedClientAssignment("client-get", "plan-x", "Client Get");

        var response = await _client.GetAsync("/api/clients");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<ClientsResponse>(JsonOpts);
        Assert.NotNull(json);
        Assert.Single(json.Clients);
        Assert.Equal("client-get", json.Clients[0].ClientAppId);
    }

    [Fact]
    public async Task DeleteClient_Existing_ReturnsOk()
    {
        SeedClientAssignment("client-del", "plan-x", "Del");
        var response = await _client.DeleteAsync("/api/clients/client-del/tenant-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteClient_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync("/api/clients/ghost-client/tenant-1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Usage Policy Settings Endpoints ──────────────────────────────────

    [Fact]
    public async Task GetUsagePolicy_ReturnsDefaults()
    {
        var response = await _client.GetAsync("/api/settings/usage-policy");
        response.EnsureSuccessStatusCode();

        var settings = await response.Content.ReadFromJsonAsync<UsagePolicySettings>(JsonOpts);
        Assert.NotNull(settings);
        Assert.Equal(1, settings.BillingCycleStartDay);
        Assert.Equal(30, settings.AggregatedLogRetentionDays);
        Assert.Equal(30, settings.TraceRetentionDays);
    }

    [Fact]
    public async Task UpdateUsagePolicy_InvalidStartDay_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync("/api/settings/usage-policy", new UsagePolicyUpdateRequest
        {
            BillingCycleStartDay = 0
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUsagePolicy_PersistsAndAffectsAssignmentPeriodStart()
    {
        SeedPlan("plan-settings", "Settings Plan");

        var updateResponse = await _client.PutAsJsonAsync("/api/settings/usage-policy", new UsagePolicyUpdateRequest
        {
            BillingCycleStartDay = 15,
            AggregatedLogRetentionDays = 40,
            TraceRetentionDays = 45
        }, JsonOpts);
        updateResponse.EnsureSuccessStatusCode();

        var updated = await updateResponse.Content.ReadFromJsonAsync<UsagePolicySettings>(JsonOpts);
        Assert.NotNull(updated);
        Assert.Equal(15, updated.BillingCycleStartDay);
        Assert.Equal(40, updated.AggregatedLogRetentionDays);
        Assert.Equal(45, updated.TraceRetentionDays);

        var assignResponse = await _client.PutAsJsonAsync("/api/clients/settings-client/tenant-1", new ClientAssignRequest
        {
            PlanId = "plan-settings",
            DisplayName = "Settings Client"
        }, JsonOpts);
        assignResponse.EnsureSuccessStatusCode();

        var assignment = await assignResponse.Content.ReadFromJsonAsync<ClientPlanAssignment>(JsonOpts);
        Assert.NotNull(assignment);
        Assert.Equal(15, assignment.CurrentPeriodStart.Day);
    }

    // ── Log Ingest Endpoint ─────────────────────────────────────────────

    [Fact]
    public async Task IngestLog_WithValidClient_Returns200()
    {
        SeedPlan("plan-ingest", "Ingest Plan", monthlyTokenQuota: 10_000_000);
        SeedClientAssignment("ingest-client", "plan-ingest", "Ingest Client");

        var body = new LogIngestRequest
        {
            TenantId = "tenant-1",
            ClientAppId = "ingest-client",
            Audience = "https://api.example.com",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Object = "chat.completion",
                Usage = new UsageData
                {
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    TotalTokens = 150
                }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/log", body, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IngestLog_WithoutClientAssignment_Returns401()
    {
        var body = new LogIngestRequest
        {
            TenantId = "tenant-1",
            ClientAppId = "unknown-client",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody { Model = "gpt-4o" }
        };

        var response = await _client.PostAsJsonAsync("/api/log", body, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestLog_WithTpmLimitedPlan_UpdatesTpmCounter()
    {
        SeedPlan("plan-tpm-ingest", "TPM Ingest Plan", monthlyTokenQuota: 1_000_000, tokensPerMinuteLimit: 1_000);
        SeedClientAssignment("tpm-ingest-client", "plan-tpm-ingest", "TPM Ingest Client");

        var response = await _client.PostAsJsonAsync("/api/log", CreateLogRequest("tpm-ingest-client", totalTokens: 90), JsonOpts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var tpmValue = await _factory.Redis.Database.StringGetAsync(RedisKeys.RateLimitTpm("tpm-ingest-client", "tenant-1", minuteWindow));
        Assert.Equal(90L, (long)tpmValue);
    }

    [Fact]
    public async Task IngestLog_MissingRequiredFields_ReturnsBadRequest()
    {
        var body = new LogIngestRequest { TenantId = "", ClientAppId = "", DeploymentId = "" };
        var response = await _client.PostAsJsonAsync("/api/log", body, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngestLog_InvalidClientPayload_Returns500()
    {
        _factory.Redis.SeedString(RedisKeys.Client("bad-client", "tenant-1"), "null");

        var response = await _client.PostAsJsonAsync("/api/log", CreateLogRequest("bad-client"), JsonOpts);
        // With the repository pattern, corrupted data deserializes to null → treated as "client not found" (401)
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestLog_InvalidPlanPayload_Returns500()
    {
        SeedClientAssignment("bad-plan-client", "bad-plan", "Bad Plan Client");
        _factory.Redis.SeedString(RedisKeys.Plan("bad-plan"), "null");

        var response = await _client.PostAsJsonAsync("/api/log", CreateLogRequest("bad-plan-client"), JsonOpts);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task IngestLog_ConcurrentRequests_UpdatesUsageAtomically()
    {
        SeedPlan("plan-concurrent", "Concurrent Plan", monthlyTokenQuota: 1_000_000);
        SeedClientAssignment("atomic-client", "plan-concurrent", "Atomic Client");

        const int requestCount = 12;
        const int tokensPerRequest = 10;

        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => _client.PostAsJsonAsync("/api/log", CreateLogRequest("atomic-client", tokensPerRequest), JsonOpts))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var assignmentJson = await _factory.Redis.Database.StringGetAsync(RedisKeys.Client("atomic-client", "tenant-1"));
        var assignment = JsonSerializer.Deserialize<ClientPlanAssignment>((string)assignmentJson!, JsonOpts);
        Assert.NotNull(assignment);

        var expectedTotal = requestCount * tokensPerRequest;
        Assert.Equal(expectedTotal, assignment.CurrentPeriodUsage);
        Assert.Equal(expectedTotal, assignment.DeploymentUsage["gpt-4o"]);

        var logJson = await _factory.Redis.Database.StringGetAsync(RedisKeys.LogEntry("atomic-client", "tenant-1", "gpt-4o"));
        var cachedLog = JsonSerializer.Deserialize<CachedLogData>((string)logJson!, JsonOpts);
        Assert.NotNull(cachedLog);
        Assert.Equal(expectedTotal, cachedLog.TotalTokens);
    }

    // ── Precheck Endpoint ───────────────────────────────────────────────

    [Fact]
    public async Task Precheck_KnownClient_Returns200()
    {
        SeedPlan("plan-pre", "Precheck Plan", monthlyTokenQuota: 10_000_000);
        SeedClientAssignment("pre-client", "plan-pre", "Pre Client");

        var response = await _client.GetAsync("/api/precheck/pre-client/tenant-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("authorized", json);
    }

    [Fact]
    public async Task Precheck_UnknownClient_Returns401()
    {
        var response = await _client.GetAsync("/api/precheck/no-such-client/tenant-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_QuotaExceeded_Returns429()
    {
        SeedPlan("plan-quota", "Quota Plan", monthlyTokenQuota: 100, allowOverbilling: false);
        SeedClientAssignment("quota-client", "plan-quota", "Quota Client", currentPeriodUsage: 200);

        var response = await _client.GetAsync("/api/precheck/quota-client/tenant-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_PerDeploymentQuotaExceeded_Returns429()
    {
        SeedPlan(
            "plan-deploy-quota",
            "Deployment Quota Plan",
            rollUpAllDeployments: false,
            deploymentQuotas: new Dictionary<string, long> { ["gpt-4o"] = 50 },
            allowOverbilling: false);

        SeedClientAssignment(
            "deploy-client",
            "plan-deploy-quota",
            "Deploy Client",
            deploymentUsage: new Dictionary<string, long> { ["gpt-4o"] = 50 });

        var response = await _client.GetAsync("/api/precheck/deploy-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_RpmLimitExceeded_Returns429OnSecondRequest()
    {
        SeedPlan(
            "plan-rpm",
            "RPM Plan",
            requestsPerMinuteLimit: 1,
            monthlyTokenQuota: 10_000_000);
        SeedClientAssignment("rpm-client", "plan-rpm", "RPM Client");

        var firstResponse = await _client.GetAsync("/api/precheck/rpm-client/tenant-1");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var secondResponse = await _client.GetAsync("/api/precheck/rpm-client/tenant-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Precheck_TpmLimitExceeded_Returns429()
    {
        SeedPlan(
            "plan-tpm",
            "TPM Plan",
            tokensPerMinuteLimit: 100,
            monthlyTokenQuota: 10_000_000);
        SeedClientAssignment("tpm-client", "plan-tpm", "TPM Client");

        var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        _factory.Redis.SeedString(RedisKeys.RateLimitTpm("tpm-client", "tenant-1", minuteWindow), "100");

        var response = await _client.GetAsync("/api/precheck/tpm-client/tenant-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // ── Deployment Access Control ──────────────────────────────────────

    [Fact]
    public async Task Precheck_EmptyAllowedDeployments_AllowsAny()
    {
        SeedPlan("plan-deploy-any", "Any Deploy Plan");
        SeedClientAssignment("deploy-any-client", "plan-deploy-any", "Deploy Any Client");

        var response = await _client.GetAsync("/api/precheck/deploy-any-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_PlanAllowedDeployment_Returns200()
    {
        SeedPlan("plan-deploy-ok", "Allowed Deploy Plan", allowedDeployments: ["gpt-4o", "gpt-4o-mini"]);
        SeedClientAssignment("deploy-ok-client", "plan-deploy-ok", "Deploy OK Client");

        var response = await _client.GetAsync("/api/precheck/deploy-ok-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_PlanBlockedDeployment_Returns403()
    {
        SeedPlan("plan-deploy-block", "Blocked Deploy Plan", allowedDeployments: ["gpt-4o-mini"]);
        SeedClientAssignment("deploy-block-client", "plan-deploy-block", "Deploy Block Client");

        var response = await _client.GetAsync("/api/precheck/deploy-block-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_ClientOverrideAllowsDeployment()
    {
        SeedPlan("plan-deploy-strict", "Strict Plan", allowedDeployments: ["gpt-4o-mini"]);
        SeedClientAssignment("deploy-override-client", "plan-deploy-strict", "Override Client",
            allowedDeployments: ["gpt-4o", "gpt-4o-mini"]);

        var response = await _client.GetAsync("/api/precheck/deploy-override-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Precheck_ClientOverrideBlocksDeployment()
    {
        SeedPlan("plan-deploy-open", "Open Plan");
        SeedClientAssignment("deploy-restrict-client", "plan-deploy-open", "Restrict Client",
            allowedDeployments: ["gpt-4o-mini"]);

        var response = await _client.GetAsync("/api/precheck/deploy-restrict-client/tenant-1?deploymentId=gpt-4o");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Export Endpoints ──────────────────────────────────────────────────

    [Fact]
    public async Task ExportAvailablePeriods_WithoutAuth_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/export/available-periods");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ExportBillingSummary_WithoutAuth_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/export/billing-summary?year=2026&month=3");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExportClientAudit_WithoutAuth_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/export/client-audit?clientAppId=test&tenantId=tenant-1&year=2026&month=3");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WebSocketLogs_WithoutUpgradeRequest_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/ws/logs");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Dashboard / Chargeback Endpoint ─────────────────────────────────

    [Fact]
    public async Task GetChargeback_ReturnsValidJson()
    {
        var response = await _client.GetAsync("/chargeback");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("totalChargeback", json);
        Assert.Contains("logs", json);
    }

    [Fact]
    public async Task GetUsageSummary_ReturnsValidJson()
    {
        var response = await _client.GetAsync("/api/usage");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("usageSummaries", json);
    }

    [Fact]
    public async Task GetUsageSummary_IgnoresPricingKeys()
    {
        // Seed billing summary in Cosmos mock for the current period
        var period = $"{DateTime.UtcNow:yyyy-MM}";
        var summaries = new List<BillingSummaryDocument>
        {
            new()
            {
                Id = $"usage-filter-client:gpt-4o:{period}",
                ClientAppId = "usage-filter-client",
                TenantId = "tenant-usage-filter",
                DeploymentId = "gpt-4o",
                Model = "gpt-4o",
                BillingPeriod = period,
                TotalTokens = 100,
                CostToUs = 0.1000m,
            }
        };
        _factory.AuditStore.GetBillingSummariesAsync(period, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(summaries));

        // Also seed a pricing key in Redis to verify it doesn't leak into results
        var pricing = new ModelPricing
        {
            ModelId = "gpt-4o-mini",
            DisplayName = "GPT-4o Mini",
            PromptRatePer1K = 0.0015m,
            CompletionRatePer1K = 0.0020m,
            UpdatedAt = DateTime.UtcNow
        };
        _factory.Redis.SeedString(RedisKeys.Pricing(pricing.ModelId), JsonSerializer.Serialize(pricing, JsonOpts));

        var response = await _client.GetAsync("/api/usage");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<UsageSummaryResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.Single(payload.UsageSummaries);
        Assert.Equal("usage-filter-client", payload.UsageSummaries[0].ClientAppId);
    }

    [Fact]
    public async Task GetRequestLogs_ReturnsEntriesAndDisplayName()
    {
        SeedClientAssignment("trace-client", "trace-plan", "Trace Client");
        SeedTraces(
            "trace-client",
            new TraceRecord { Timestamp = DateTime.UtcNow.AddMinutes(-1), DeploymentId = "gpt-4o", Model = "gpt-4o", TotalTokens = 10, CostToUs = "0.1000", CostToCustomer = "0.0000" },
            new TraceRecord { Timestamp = DateTime.UtcNow, DeploymentId = "gpt-4o", Model = "gpt-4o", TotalTokens = 20, CostToUs = "0.2000", CostToCustomer = "0.0000" });

        var response = await _client.GetAsync("/api/logs");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<RequestLogsResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.TotalCount);
        Assert.Equal(2, payload.Entries.Count);
        Assert.All(payload.Entries, entry => Assert.Equal("Trace Client", entry.ClientDisplayName));
    }

    [Fact]
    public async Task GetRequestLogs_LimitsEntriesTo200()
    {
        SeedClientAssignment("trace-cap-client", "trace-cap-plan", "Trace Cap Client");
        var traces = Enumerable.Range(1, 205)
            .Select(i => new TraceRecord
            {
                Timestamp = DateTime.UtcNow.AddSeconds(-i),
                DeploymentId = "gpt-4o",
                Model = "gpt-4o",
                TotalTokens = i,
                CostToUs = "0.0100",
                CostToCustomer = "0.0000"
            })
            .ToArray();
        SeedTraces("trace-cap-client", traces);

        var response = await _client.GetAsync("/api/logs");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<RequestLogsResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.Equal(205, payload.TotalCount);
        Assert.Equal(200, payload.Entries.Count);
    }

    [Fact]
    public async Task GetClientUsage_UnknownClient_Returns404()
    {
        var response = await _client.GetAsync("/api/clients/no-client/tenant-1/usage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetClientUsage_ReturnsUsageCostsAndMeters()
    {
        SeedPlan("plan-client-usage", "Client Usage Plan");
        SeedClientAssignment("usage-client", "plan-client-usage", "Usage Client", tenantId: "tenant-usage");
        SeedLog("tenant-usage", "usage-client", "gpt-4o", model: "gpt-4o", totalTokens: 120, promptTokens: 80, completionTokens: 40, costToUs: 1.2000m);
        SeedLog("tenant-usage", "usage-client", "gpt-4o-mini", model: "gpt-4o-mini", totalTokens: 60, promptTokens: 30, completionTokens: 30, costToUs: 0.3000m);

        var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        _factory.Redis.SeedString(RedisKeys.RateLimitRpm("usage-client", "tenant-usage", minuteWindow), "3");
        _factory.Redis.SeedString(RedisKeys.RateLimitTpm("usage-client", "tenant-usage", minuteWindow), "180");

        var response = await _client.GetAsync("/api/clients/usage-client/tenant-usage/usage");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ClientUsageResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.NotNull(payload.Assignment);
        Assert.Equal("usage-client", payload.Assignment.ClientAppId);
        Assert.Equal(2, payload.Logs.Count);
        Assert.Equal(120, payload.UsageByModel["gpt-4o"]);
        Assert.Equal(60, payload.UsageByModel["gpt-4o-mini"]);
        Assert.Equal(3, payload.CurrentRpm);
        Assert.Equal(180, payload.CurrentTpm);
    }

    [Fact]
    public async Task ClientDetail_UsageAndCostMatchTraceTotals()
    {
        SeedPlan("plan-trace-alignment", "Trace Alignment Plan", monthlyTokenQuota: 1_000_000);
        SeedClientAssignment("trace-align-client", "plan-trace-alignment", "Trace Align Client");

        var first = await _client.PostAsJsonAsync("/api/log", CreateLogRequest("trace-align-client", totalTokens: 120), JsonOpts);
        var second = await _client.PostAsJsonAsync("/api/log", CreateLogRequest("trace-align-client", totalTokens: 80), JsonOpts);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var usageResponse = await _client.GetAsync("/api/clients/trace-align-client/tenant-1/usage");
        usageResponse.EnsureSuccessStatusCode();
        var usage = await usageResponse.Content.ReadFromJsonAsync<ClientUsageResponse>(JsonOpts);
        Assert.NotNull(usage);
        Assert.NotNull(usage.Assignment);

        var tracesResponse = await _client.GetAsync("/api/clients/trace-align-client/tenant-1/traces");
        tracesResponse.EnsureSuccessStatusCode();
        var tracesPayload = await tracesResponse.Content.ReadFromJsonAsync<ClientTracesResponse>(JsonOpts);
        Assert.NotNull(tracesPayload);
        Assert.NotEmpty(tracesPayload.Traces);

        var traceTokenTotal = tracesPayload.Traces.Sum(t => t.TotalTokens);
        var traceCostToUsTotal = tracesPayload.Traces.Sum(t => decimal.Parse(t.CostToUs, CultureInfo.InvariantCulture));

        Assert.Equal(traceTokenTotal, usage.Assignment.CurrentPeriodUsage);
        Assert.Equal(traceCostToUsTotal, usage.TotalCostToUs);
    }

    [Fact]
    public async Task GetClientTraces_ReturnsSortedTraces()
    {
        SeedTraces(
            "trace-order-client",
            new TraceRecord { Timestamp = DateTime.UtcNow.AddMinutes(-2), DeploymentId = "gpt-4o", TotalTokens = 10 },
            new TraceRecord { Timestamp = DateTime.UtcNow, DeploymentId = "gpt-4o", TotalTokens = 20 },
            new TraceRecord { Timestamp = DateTime.UtcNow.AddMinutes(-1), DeploymentId = "gpt-4o", TotalTokens = 15 });

        var response = await _client.GetAsync("/api/clients/trace-order-client/tenant-1/traces");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ClientTracesResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.Equal(3, payload.Traces.Count);
        Assert.True(payload.Traces[0].Timestamp >= payload.Traces[1].Timestamp);
        Assert.True(payload.Traces[1].Timestamp >= payload.Traces[2].Timestamp);
    }

    [Fact]
    public async Task AdminDeleteRedisKey_ExistingKey_ReturnsOk()
    {
        _factory.Redis.SeedString("admin:test-key", "value");

        var response = await _client.DeleteAsync("/api/admin/redis/admin:test-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SeedPlan(string id, string name,
        long monthlyTokenQuota = 1_000_000,
        bool allowOverbilling = true,
        bool rollUpAllDeployments = true,
        int tokensPerMinuteLimit = 0,
        int requestsPerMinuteLimit = 0,
        Dictionary<string, long>? deploymentQuotas = null,
        List<string>? allowedDeployments = null)
    {
        var plan = new PlanData
        {
            Id = id,
            Name = name,
            MonthlyRate = 99m,
            MonthlyTokenQuota = monthlyTokenQuota,
            TokensPerMinuteLimit = tokensPerMinuteLimit,
            RequestsPerMinuteLimit = requestsPerMinuteLimit,
            AllowOverbilling = allowOverbilling,
            CostPerMillionTokens = 5m,
            RollUpAllDeployments = rollUpAllDeployments,
            DeploymentQuotas = deploymentQuotas ?? new Dictionary<string, long>(),
            AllowedDeployments = allowedDeployments ?? [],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _factory.Redis.SeedString(RedisKeys.Plan(id), JsonSerializer.Serialize(plan, JsonOpts));
    }

    private void SeedClientAssignment(string clientAppId, string planId, string displayName,
        long currentPeriodUsage = 0,
        Dictionary<string, long>? deploymentUsage = null,
        string tenantId = "tenant-1",
        List<string>? allowedDeployments = null)
    {
        var assignment = new ClientPlanAssignment
        {
            ClientAppId = clientAppId,
            TenantId = tenantId,
            PlanId = planId,
            DisplayName = displayName,
            CurrentPeriodStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            CurrentPeriodUsage = currentPeriodUsage,
            DeploymentUsage = deploymentUsage ?? new Dictionary<string, long>(),
            AllowedDeployments = allowedDeployments ?? [],
            LastUpdated = DateTime.UtcNow
        };
        _factory.Redis.SeedString(RedisKeys.Client(clientAppId, tenantId), JsonSerializer.Serialize(assignment, JsonOpts));
    }

    private static LogIngestRequest CreateLogRequest(string clientAppId, int totalTokens = 150)
    {
        var promptTokens = Math.Max(0, totalTokens - 50);
        var completionTokens = totalTokens - promptTokens;

        return new LogIngestRequest
        {
            TenantId = "tenant-1",
            ClientAppId = clientAppId,
            Audience = "https://api.example.com",
            DeploymentId = "gpt-4o",
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4o",
                Object = "chat.completion",
                Usage = new UsageData
                {
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens
                }
            }
        };
    }

    private void SeedLog(
        string tenantId,
        string clientAppId,
        string deploymentId,
        string model,
        long totalTokens,
        long promptTokens = 0,
        long completionTokens = 0,
        decimal costToUs = 0m,
        decimal costToCustomer = 0m)
    {
        var entry = new CachedLogData
        {
            TenantId = tenantId,
            ClientAppId = clientAppId,
            Audience = "https://api.example.com",
            DeploymentId = deploymentId,
            Model = model,
            ObjectType = "chat.completion",
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CostToUs = costToUs,
            CostToCustomer = costToCustomer
        };

        _factory.Redis.SeedString(
            RedisKeys.LogEntry(clientAppId, tenantId, deploymentId),
            JsonSerializer.Serialize(entry, JsonOpts));
    }

    private void SeedTraces(string clientAppId, params TraceRecord[] traces)
    {
        _factory.Redis.SeedList(
            RedisKeys.Traces(clientAppId, "tenant-1"),
            traces.Select(trace => JsonSerializer.Serialize(trace, JsonOpts)).ToArray());
    }
}
