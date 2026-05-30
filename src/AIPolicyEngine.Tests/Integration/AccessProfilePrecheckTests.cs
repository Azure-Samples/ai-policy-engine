using System.Net;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Tests.Services.AccessProfiles;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static AIPolicyEngine.Tests.Services.AccessProfiles.AccessProfileTestSupport;

namespace AIPolicyEngine.Tests.Integration;

public sealed class AccessProfilePrecheckTests : IClassFixture<ChargebackApiFactory>
{
    private readonly ChargebackApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;
    private static readonly string[] ShippedTemplateIds =
    [
        "entra-jwt-ai",
        "entra-jwt-ai-dlp",
        "subscription-key-ai",
        "subscription-key-ai-dlp",
        "entra-jwt-rest",
        "keycloak-jwt-ai",
        "keycloak-jwt-ai-dlp",
        "keycloak-jwt-rest"
    ];

    private static readonly string[] PrecheckTemplateIds =
    [
        "entra-jwt-ai",
        "entra-jwt-ai-dlp",
        "subscription-key-ai",
        "subscription-key-ai-dlp",
        "keycloak-jwt-ai",
        "keycloak-jwt-ai-dlp",
        "keycloak-jwt-rest"
    ];

    private const string ClientAppId = "access-client";
    private const string TenantId = "tenant-1";

    public AccessProfilePrecheckTests(ChargebackApiFactory factory)
    {
        _factory = factory;
        _factory.Redis.Clear();
    }

    [Fact]
    public async Task Precheck_WithApiAndOperation_InvokesResolver_AndReturnsAccessProfileAndPlanId()
    {
        SeedPlan(CreatePlan(id: "profile-plan", name: "Profile Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        var (resolverProxy, tracker) = CreateResolverProxy((clientAppId, tenantId, apiId, operationId) =>
            new ResolvedAccessSnapshot(
                PlanId: "profile-plan",
                RoutingPolicyId: null,
                AllowedDeployments: ["gpt-4o"],
                SourceProfileId: $"ap:{clientAppId}:{tenantId}:{apiId}:{operationId}"));

        using var client = CreateClient(resolverProxy);
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api&operationId=chat");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(tracker.Calls);
        Assert.Equal((ClientAppId, TenantId, "openai-api", "chat"), tracker.Calls[0]);
        Assert.Equal("profile-plan", json.RootElement.GetProperty("planId").GetString());
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:chat", json.RootElement.GetProperty("accessProfileId").GetString());
    }

    [Fact]
    public async Task Precheck_WithoutApiId_UsesLegacyPath_AndOmitsAccessProfileId()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", name: "Legacy Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        using var client = CreateClient();
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("legacy-plan", json.RootElement.GetProperty("planId").GetString());
        Assert.False(json.RootElement.TryGetProperty("accessProfileId", out var accessProfileId) && accessProfileId.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Precheck_WithApiIdAndNoProfile_FallsBackToLegacyAssignment()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", name: "Legacy Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        var (resolverProxy, tracker) = CreateResolverProxy((_, _, _, _) => null);
        using var client = CreateClient(resolverProxy);

        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(tracker.Calls);
        Assert.Equal("legacy-plan", json.RootElement.GetProperty("planId").GetString());
        Assert.False(json.RootElement.TryGetProperty("accessProfileId", out var accessProfileId) && accessProfileId.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Precheck_DisabledOperationProfile_IsSkipped_InFavorOfApiProfile()
    {
        SeedPlan(CreatePlan(id: "api-plan", name: "API Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        using var resolverHarness = CreateResolverHarness(
        [
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", "chat", "disabled-plan", enabled: false),
            CreateAccessProfile(ClientAppId, TenantId, "openai-api", null, "api-plan")
        ],
        CreateLegacyAssignment(planId: "legacy-plan"));

        using var client = CreateClient(resolverHarness.Instance);
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api&operationId=chat");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("api-plan", json.RootElement.GetProperty("planId").GetString());
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:_all", json.RootElement.GetProperty("accessProfileId").GetString());
    }

    [Fact]
    public async Task Precheck_ResponseIncludesAllowedDeployments_FromResolvedProfile()
    {
        SeedPlan(CreatePlan(id: "profile-plan", name: "Profile Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        var (resolverProxy, _) = CreateResolverProxy((clientAppId, tenantId, apiId, operationId) =>
            new ResolvedAccessSnapshot(
                PlanId: "profile-plan",
                RoutingPolicyId: null,
                AllowedDeployments: ["gpt-4o", "gpt-4o-mini"],
                SourceProfileId: $"ap:{clientAppId}:{tenantId}:{apiId}:{operationId ?? "_all"}"));

        using var client = CreateClient(resolverProxy);
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["gpt-4o", "gpt-4o-mini"],
            json.RootElement.GetProperty("allowedDeployments").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Precheck_BackwardCompatibleLegacyCallers_KeepCurrentResponseShape()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", name: "Legacy Plan"));
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        using var client = CreateClient();
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("authorized", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(ClientAppId, json.RootElement.GetProperty("clientAppId").GetString());
        Assert.Equal(TenantId, json.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("Legacy Plan", json.RootElement.GetProperty("plan").GetString());
        Assert.Equal("gpt-4o", json.RootElement.GetProperty("requestedDeployment").GetString());
    }

    [Fact]
    public async Task Precheck_BlockedProfile_Returns403()
    {
        SeedClientAssignment(CreateLegacyAssignment(planId: "legacy-plan"));

        var (resolverProxy, tracker) = CreateResolverProxy((clientAppId, tenantId, apiId, operationId) =>
            new ResolvedAccessSnapshot(
                PlanId: "",
                RoutingPolicyId: null,
                AllowedDeployments: [],
                SourceProfileId: $"ap:{clientAppId}:{tenantId}:{apiId}:{operationId ?? "_all"}",
                Blocked: true));

        using var client = CreateClient(resolverProxy);
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api&operationId=chat");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(tracker.Calls);
        Assert.Equal("access-profile-blocked", json.RootElement.GetProperty("deniedBy").GetString());
        Assert.Equal($"ap:{ClientAppId}:{TenantId}:openai-api:chat", json.RootElement.GetProperty("accessProfileId").GetString());
    }

    [Fact]
    public async Task Precheck_BlockedProfile_Returns403_EvenWithoutClientAssignment()
    {
        var (resolverProxy, _) = CreateResolverProxy((clientAppId, tenantId, apiId, operationId) =>
            new ResolvedAccessSnapshot(
                PlanId: "",
                RoutingPolicyId: null,
                AllowedDeployments: [],
                SourceProfileId: $"ap:{clientAppId}:{tenantId}:{apiId}:{operationId ?? "_all"}",
                Blocked: true));

        using var client = CreateClient(resolverProxy);
        var response = await client.GetAsync($"/api/precheck/{ClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("access-profile-blocked", json.RootElement.GetProperty("deniedBy").GetString());
    }

    [Fact]
    public void TemplateRendering_ApiIdVariableExtraction()
    {
        foreach (var templateId in ShippedTemplateIds)
        {
            var policyXml = ReadTemplatePolicy(templateId);
            Assert.Contains("<set-variable name=\"apiIdValue\" value=\"@(context.Api.Id)\" />", policyXml, StringComparison.Ordinal);
            Assert.Contains("<set-variable name=\"operationIdValue\" value=\"@(context.Operation.Id)\" />", policyXml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TemplateRendering_PrecheckUrlCarriesApiAndOperation()
    {
        foreach (var templateId in PrecheckTemplateIds)
        {
            var policyXml = ReadTemplatePolicy(templateId);
            // AI templates: ?deploymentId=...&apiId=...&operationId=...
            // REST templates: ?apiId=...&operationId=... (no deploymentId)
            Assert.Contains("apiId={(string)context.Variables[\"apiIdValue\"]}", policyXml, StringComparison.Ordinal);
            Assert.Contains("operationId={(string)context.Variables[\"operationIdValue\"]}", policyXml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TemplateRendering_LogPayloadCarriesAccessProfileMetadata()
    {
        foreach (var templateId in ShippedTemplateIds)
        {
            var policyXml = ReadTemplatePolicy(templateId);
            Assert.Contains("JProperty(\"accessProfileId\"", policyXml, StringComparison.Ordinal);
            Assert.Contains("GetValueOrDefault<string>(\"accessProfileId\")", policyXml, StringComparison.Ordinal);
            Assert.Contains("JProperty(\"planId\"", policyXml, StringComparison.Ordinal);
            Assert.Contains("GetValueOrDefault<string>(\"resolvedPlanId\")", policyXml, StringComparison.Ordinal);
            Assert.Contains("JProperty(\"apiId\"", policyXml, StringComparison.Ordinal);
            Assert.Contains("GetValueOrDefault<string>(\"apiIdValue\")", policyXml, StringComparison.Ordinal);
            Assert.Contains("JProperty(\"operationId\"", policyXml, StringComparison.Ordinal);
            Assert.Contains("GetValueOrDefault<string>(\"operationIdValue\")", policyXml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TemplateRendering_TemplateVersionBump()
    {
        foreach (var templateId in ShippedTemplateIds)
        {
            using var manifestJson = JsonDocument.Parse(ReadTemplateManifest(templateId));
            Assert.Equal("1.1", manifestJson.RootElement.GetProperty("version").GetString());
        }
    }

    private HttpClient CreateClient(object? resolver = null)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                if (resolver is not null)
                {
                    var resolverType = RequireType("IAccessProfileResolver");
                    RemoveService(services, resolverType);
                    services.AddSingleton(resolverType, resolver);
                }
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    private static string ReadTemplatePolicy(string templateId)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "policies", "templates", templateId, "policy.xml"));

    private static string ReadTemplateManifest(string templateId)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "policies", "templates", templateId, "template.json"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "policies", "templates")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with policies\\templates was not found.");
    }

    private void SeedPlan(PlanData plan)
        => _factory.Redis.SeedString(RedisKeys.Plan(plan.Id), JsonSerializer.Serialize(plan, JsonOpts));

    private void SeedClientAssignment(ClientPlanAssignment assignment)
        => _factory.Redis.SeedString($"client:{assignment.ClientAppId}:{assignment.TenantId}", JsonSerializer.Serialize(assignment, JsonOpts));

    private static PlanData CreatePlan(string id, string name) => new()
    {
        Id = id,
        Name = name,
        MonthlyRate = 99m,
        MonthlyTokenQuota = 1_000_000,
        TokensPerMinuteLimit = 0,
        RequestsPerMinuteLimit = 0,
        AllowOverbilling = true,
        CostPerMillionTokens = 5m,
        RollUpAllDeployments = true
    };

    private static ClientPlanAssignment CreateLegacyAssignment(string planId) => new()
    {
        Id = $"{ClientAppId}:{TenantId}",
        ClientAppId = ClientAppId,
        TenantId = TenantId,
        PlanId = planId,
        DisplayName = "Legacy Client",
        CurrentPeriodStart = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc),
        AllowedDeployments = []
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static void RemoveService(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }
}
