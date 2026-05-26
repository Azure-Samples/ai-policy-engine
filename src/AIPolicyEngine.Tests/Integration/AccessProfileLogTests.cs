using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AIPolicyEngine.Tests.Integration;

public sealed class AccessProfileLogTests : IClassFixture<ChargebackApiFactory>
{
    private readonly ChargebackApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = JsonConfig.Default;
    private const string ClientAppId = "log-access-client";
    private const string TenantId = "tenant-1";

    public AccessProfileLogTests(ChargebackApiFactory factory)
    {
        _factory = factory;
        _factory.Redis.Clear();
    }

    [Fact]
    public async Task LogRequest_WithPlanId_UsesPlanIdForPlanLookup()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", useMultiplierBilling: false));
        SeedPlan(CreatePlan(id: "profile-plan", useMultiplierBilling: true));
        SeedClientAssignment(CreateClientAssignment(planId: "legacy-plan"));

        using var client = CreateClient(out _);
        var response = await client.PostAsync("/api/log", CreateJsonContent(CreateLogRequest(planId: "profile-plan")));
        var updated = await ReadClientAssignmentAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(updated);
        Assert.True(updated!.CurrentPeriodRequests > 0m, "Resolved planId should enable multiplier billing request accounting.");
    }

    [Fact]
    public async Task LogRequest_WithoutPlanId_FallsBackToClientPlanAssignmentLookup()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", useMultiplierBilling: true));
        SeedClientAssignment(CreateClientAssignment(planId: "legacy-plan"));

        using var client = CreateClient(out _);
        var response = await client.PostAsync("/api/log", CreateJsonContent(CreateLogRequest()));
        var updated = await ReadClientAssignmentAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(updated);
        Assert.True(updated!.CurrentPeriodRequests > 0m);
    }

    [Fact]
    public async Task LogRequest_WithAccessProfileId_RecordsItInAuditEntry()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", useMultiplierBilling: false));
        SeedClientAssignment(CreateClientAssignment(planId: "legacy-plan"));

        using var client = CreateClient(out var channel);
        var response = await client.PostAsync("/api/log", CreateJsonContent(CreateLogRequest(accessProfileId: "ap:client:tenant:openai-api:_all", planId: "legacy-plan", apiId: "openai-api", operationId: "chat")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(channel.Reader.TryRead(out var auditItem), "Audit channel should receive an item.");
        Assert.Equal("ap:client:tenant:openai-api:_all", auditItem!.GetType().GetProperty("AccessProfileId")?.GetValue(auditItem)?.ToString());
    }

    [Fact]
    public async Task LogRequest_WithoutNewFields_LegacyCallerStillWorks()
    {
        SeedPlan(CreatePlan(id: "legacy-plan", useMultiplierBilling: false));
        SeedClientAssignment(CreateClientAssignment(planId: "legacy-plan"));

        using var client = CreateClient(out _);
        var response = await client.PostAsync("/api/log", CreateJsonContent(CreateLogRequest()));
        var updated = await ReadClientAssignmentAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(150, updated!.CurrentPeriodUsage);
    }

    private HttpClient CreateClient(out Channel<AuditLogItem> auditChannel)
    {
        var channel = Channel.CreateUnbounded<AuditLogItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        auditChannel = channel;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                RemoveHostedService<AuditLogWriter>(services);
                RemoveService<Channel<AuditLogItem>>(services);
                services.AddSingleton(channel);
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

    private async Task<ClientPlanAssignment?> ReadClientAssignmentAsync()
    {
        var value = await _factory.Redis.Database.StringGetAsync($"client:{ClientAppId}:{TenantId}");
        return !value.HasValue ? null : JsonSerializer.Deserialize<ClientPlanAssignment>((string)value!, JsonOpts);
    }

    private static StringContent CreateJsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

    private static LogIngestRequest CreateLogRequest(string? accessProfileId = null, string? planId = null, string? apiId = null, string? operationId = null)
        => new()
        {
            TenantId = TenantId,
            ClientAppId = ClientAppId,
            Audience = "api://engine",
            DeploymentId = "gpt-4.1",
            AccessProfileId = accessProfileId,
            PlanId = planId,
            ApiId = apiId,
            OperationId = operationId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ResponseBody = new OpenAiResponseBody
            {
                Model = "gpt-4.1",
                Object = "chat.completion",
                Usage = new UsageData
                {
                    PromptTokens = 100,
                    CompletionTokens = 50,
                    TotalTokens = 150,
                    ImageTokens = 0
                }
            }
        };

    private static PlanData CreatePlan(string id, bool useMultiplierBilling) => new()
    {
        Id = id,
        Name = id,
        MonthlyRate = 99m,
        MonthlyTokenQuota = 1_000_000,
        RequestsPerMinuteLimit = 0,
        TokensPerMinuteLimit = 0,
        AllowOverbilling = true,
        CostPerMillionTokens = 5m,
        RollUpAllDeployments = true,
        UseMultiplierBilling = useMultiplierBilling,
        MonthlyRequestQuota = 100m,
        OverageRatePerRequest = 1.0m
    };

    private static ClientPlanAssignment CreateClientAssignment(string planId) => new()
    {
        Id = $"{ClientAppId}:{TenantId}",
        ClientAppId = ClientAppId,
        TenantId = TenantId,
        PlanId = planId,
        DisplayName = "Log Access Client",
        CurrentPeriodStart = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
    };

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            (descriptor.ImplementationType == typeof(T) ||
             (descriptor.ImplementationFactory?.Method.ReturnType?.IsAssignableFrom(typeof(T)) ?? false))).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }
}
