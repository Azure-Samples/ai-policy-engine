using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// Per-client usage report and trace endpoints for the client detail page.
/// </summary>
public static class ClientDetailEndpoints
{
    public static IEndpointRouteBuilder MapClientDetailEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/clients/{clientAppId}/{tenantId}/usage", GetClientUsage)
            .WithName("GetClientUsage")
            .WithDescription("Get per-customer usage report including costs and rate limits")
            .RequireAuthorization()
            .Produces<ClientUsageResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapGet("/api/clients/{clientAppId}/{tenantId}/traces", GetClientTraces)
            .WithName("GetClientTraces")
            .WithDescription("Get recent trace records for a customer")
            .RequireAuthorization()
            .Produces<ClientTracesResponse>()
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> GetClientUsage(
        string clientAppId,
        string tenantId,
        IRepository<ClientPlanAssignment> clientRepo,
        IRepository<PlanData> planRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
        ILogger<ClientUsageResponse> logger)
    {
        try
        {
            var usagePolicy = await usagePolicyStore.GetAsync();

            var clientId = $"{clientAppId}:{tenantId}";
            var assignment = await clientRepo.GetAsync(clientId);
            if (assignment is null)
                return Results.NotFound(new { error = $"Customer '{clientAppId}/{tenantId}' not found" });

            var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(DateTime.UtcNow, usagePolicy.BillingCycleStartDay);
            if (assignment.CurrentPeriodStart != expectedPeriodStart)
            {
                assignment.CurrentPeriodStart = expectedPeriodStart;
                assignment.CurrentPeriodUsage = 0;
                assignment.OverbilledTokens = 0;
                assignment.DeploymentUsage = new();
                assignment.CurrentPeriodRequests = 0;
                assignment.OverbilledRequests = 0;
                assignment.RequestsByTier = new();
            }

            PlanData? plan = null;
            if (!string.IsNullOrEmpty(assignment.PlanId))
                plan = await planRepo.GetAsync(assignment.PlanId);

            // Log data and rate limits are ephemeral — stay Redis-direct
            var db = redis.GetDatabase();
            var logKeys = redis.KeysFromAllServers(RedisKeys.CustomerLogPattern(clientAppId, tenantId));
            var logs = new List<LogEntry>();
            var usageByModel = new Dictionary<string, long>();
            decimal totalCostToUs = 0m;
            decimal totalCostToCustomer = 0m;

            foreach (var logKey in logKeys)
            {
                var logValue = await db.StringGetAsync(logKey);
                if (!logValue.HasValue) continue;

                try
                {
                    var cached = JsonSerializer.Deserialize<CachedLogData>((string)logValue!, JsonConfig.Default);
                    if (cached is null) continue;

                    logs.Add(new LogEntry
                    {
                        TenantId = cached.TenantId,
                        ClientAppId = cached.ClientAppId,
                        Audience = cached.Audience,
                        DeploymentId = cached.DeploymentId,
                        Model = cached.Model,
                        ObjectType = cached.ObjectType,
                        PromptTokens = cached.PromptTokens,
                        CompletionTokens = cached.CompletionTokens,
                        TotalTokens = cached.TotalTokens,
                        ImageTokens = cached.ImageTokens,
                        CostToUs = cached.CostToUs.ToString("F4"),
                        CostToCustomer = cached.CostToCustomer.ToString("F4"),
                        TotalCost = (cached.CostToUs + cached.CostToCustomer).ToString("F4"),
                        IsOverbilled = cached.IsOverbilled
                    });

                    var modelName = cached.Model ?? "unknown";
                    if (usageByModel.ContainsKey(modelName))
                        usageByModel[modelName] += cached.TotalTokens;
                    else
                        usageByModel[modelName] = cached.TotalTokens;

                    totalCostToUs += cached.CostToUs;
                    totalCostToCustomer += cached.CostToCustomer;
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize log data for key {Key}", logKey);
                }
            }

            var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var tpmValue = await db.StringGetAsync(RedisKeys.RateLimitTpm(clientAppId, tenantId, minuteWindow));
            var rpmValue = await db.StringGetAsync(RedisKeys.RateLimitRpm(clientAppId, tenantId, minuteWindow));

            var response = new ClientUsageResponse
            {
                Assignment = assignment,
                Plan = plan,
                Logs = logs,
                UsageByModel = usageByModel,
                CurrentTpm = tpmValue.HasValue ? (long)tpmValue : 0,
                CurrentRpm = rpmValue.HasValue ? (long)rpmValue : 0,
                TotalCostToUs = totalCostToUs,
                TotalCostToCustomer = totalCostToCustomer,
                CurrentPeriodRequests = assignment.CurrentPeriodRequests,
                OverbilledRequests = assignment.OverbilledRequests,
                RequestsByTier = assignment.RequestsByTier,
                MonthlyRequestQuota = plan?.MonthlyRequestQuota ?? 0,
                RequestUtilizationPercent = plan is not null && plan.MonthlyRequestQuota > 0
                    ? Math.Round(assignment.CurrentPeriodRequests / plan.MonthlyRequestQuota * 100, 2)
                    : -1
            };

            return Results.Json(response, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching usage for customer {ClientAppId}/{TenantId}", clientAppId, tenantId);
            return Results.Json(new { error = "Failed to fetch client usage" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetClientTraces(
        string clientAppId,
        string tenantId,
        IConnectionMultiplexer redis,
        ILogger<ClientTracesResponse> logger)
    {
        try
        {
            // Traces are ephemeral — Redis-only
            var db = redis.GetDatabase();
            var entries = await db.ListRangeAsync(RedisKeys.Traces(clientAppId, tenantId), 0, 99);

            var traces = new List<TraceRecord>();
            foreach (var entry in entries)
            {
                try
                {
                    var trace = JsonSerializer.Deserialize<TraceRecord>((string)entry!, JsonConfig.Default);
                    if (trace is not null)
                        traces.Add(trace);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize trace for customer {ClientAppId}/{TenantId}", clientAppId, tenantId);
                }
            }

            traces.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            return Results.Json(new ClientTracesResponse { Traces = traces }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching traces for customer {ClientAppId}/{TenantId}", clientAppId, tenantId);
            return Results.Json(new { error = "Failed to fetch client traces" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
