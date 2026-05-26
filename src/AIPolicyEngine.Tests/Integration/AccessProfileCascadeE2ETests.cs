using System.Net;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Tests.Services.AccessProfiles;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static AIPolicyEngine.Tests.Services.AccessProfiles.AccessProfileTestSupport;

namespace AIPolicyEngine.Tests.Integration;

public sealed class AccessProfileCascadeE2ETests : IClassFixture<ChargebackApiFactory>
{
    private readonly ChargebackApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;
    private const string TenantId = "tenant-1";
    private const string ProfileClientAppId = "cascade-client";
    private const string LegacyClientAppId = "cascade-legacy-client";

    public AccessProfileCascadeE2ETests(ChargebackApiFactory factory)
    {
        _factory = factory;
        _factory.Redis.Clear();
    }

    [Fact]
    public async Task Precheck_EndToEnd_CascadesOperationApiGlobalThenLegacy()
    {
        SeedPlan(CreatePlan("operation-plan", "Operation Plan"));
        SeedPlan(CreatePlan("api-plan", "API Plan"));
        SeedPlan(CreatePlan("global-plan", "Global Plan"));
        SeedPlan(CreatePlan("legacy-plan", "Legacy Plan"));

        var profiledAssignment = CreateLegacyAssignment(ProfileClientAppId, "legacy-plan");
        var legacyAssignment = CreateLegacyAssignment(LegacyClientAppId, "legacy-plan");
        SeedClientAssignment(profiledAssignment);
        SeedClientAssignment(legacyAssignment);

        using var resolverHarness = CreateResolverHarness(
        [
            CreateAccessProfile(ProfileClientAppId, TenantId, "_global", null, "global-plan"),
            CreateAccessProfile(ProfileClientAppId, TenantId, "openai-api", null, "api-plan"),
            CreateAccessProfile(ProfileClientAppId, TenantId, "openai-api", "chat", "operation-plan")
        ],
        [
            profiledAssignment,
            legacyAssignment
        ]);

        using var client = CreateClient(resolverHarness.Instance);

        var operationResponse = await client.GetAsync($"/api/precheck/{ProfileClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api&operationId=chat");
        var operationJson = await ReadJsonAsync(operationResponse);
        Assert.Equal(HttpStatusCode.OK, operationResponse.StatusCode);
        Assert.Equal("operation-plan", operationJson.RootElement.GetProperty("planId").GetString());
        Assert.Equal($"ap:{ProfileClientAppId}:{TenantId}:openai-api:chat", operationJson.RootElement.GetProperty("accessProfileId").GetString());

        var apiResponse = await client.GetAsync($"/api/precheck/{ProfileClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=openai-api&operationId=embeddings");
        var apiJson = await ReadJsonAsync(apiResponse);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        Assert.Equal("api-plan", apiJson.RootElement.GetProperty("planId").GetString());
        Assert.Equal($"ap:{ProfileClientAppId}:{TenantId}:openai-api:_all", apiJson.RootElement.GetProperty("accessProfileId").GetString());

        var globalResponse = await client.GetAsync($"/api/precheck/{ProfileClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=embeddings-api&operationId=create");
        var globalJson = await ReadJsonAsync(globalResponse);
        Assert.Equal(HttpStatusCode.OK, globalResponse.StatusCode);
        Assert.Equal("global-plan", globalJson.RootElement.GetProperty("planId").GetString());
        Assert.Equal($"ap:{ProfileClientAppId}:{TenantId}:_global:_all", globalJson.RootElement.GetProperty("accessProfileId").GetString());

        var legacyResponse = await client.GetAsync($"/api/precheck/{LegacyClientAppId}/{TenantId}?deploymentId=gpt-4o&apiId=embeddings-api&operationId=create");
        var legacyJson = await ReadJsonAsync(legacyResponse);
        Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
        Assert.Equal("legacy-plan", legacyJson.RootElement.GetProperty("planId").GetString());
        Assert.True(!legacyJson.RootElement.TryGetProperty("accessProfileId", out var accessProfileId) || accessProfileId.ValueKind == JsonValueKind.Null);
    }

    private HttpClient CreateClient(object resolver)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var resolverType = RequireType("IAccessProfileResolver");
                RemoveService(services, resolverType);
                services.AddSingleton(resolverType, resolver);
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        return client;
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

    private static ClientPlanAssignment CreateLegacyAssignment(string clientAppId, string planId) => new()
    {
        Id = $"{clientAppId}:{TenantId}",
        ClientAppId = clientAppId,
        TenantId = TenantId,
        PlanId = planId,
        DisplayName = "Cascade Client",
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
