using System.Text.Json;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using StackExchange.Redis;

namespace Chargeback.Api.Endpoints;

public static class PrecheckEndpoints
{
    public static IEndpointRouteBuilder MapPrecheckEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/precheck/{clientAppId}/{tenantId}", Precheck)
            .WithName("Precheck")
            .WithDescription("Pre-authorize a client+tenant request — checks plan, quota, and rate limits")
            .RequireAuthorization("ApimPolicy");
        return routes;
    }

    private static async Task<IResult> Precheck(
        string clientAppId,
        string tenantId,
        HttpContext context,
        IConnectionMultiplexer redis,
        IUsagePolicyStore usagePolicyStore,
        ILogger<PlanData> logger)
    {
        var db = redis.GetDatabase();

        // 1. Check client assignment exists
        var clientValue = await db.StringGetAsync(RedisKeys.Client(clientAppId, tenantId));
        if (!clientValue.HasValue)
        {
            return Results.Json(
                new { error = "Client not authorized — no plan assigned", clientAppId, tenantId },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var assignment = JsonSerializer.Deserialize<ClientPlanAssignment>((string)clientValue!, JsonConfig.Default);
        if (assignment is null)
        {
            logger.LogError("Invalid client assignment payload for {ClientAppId}/{TenantId}", clientAppId, tenantId);
            return Results.Json(
                new { error = "Client assignment is invalid", clientAppId, tenantId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 2. Check plan exists
        var planValue = await db.StringGetAsync(RedisKeys.Plan(assignment.PlanId));
        if (!planValue.HasValue)
        {
            return Results.Json(
                new { error = "Plan configuration not found", planId = assignment.PlanId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var plan = JsonSerializer.Deserialize<PlanData>((string)planValue!, JsonConfig.Default);
        if (plan is null)
        {
            logger.LogError("Invalid plan payload for {PlanId}", assignment.PlanId);
            return Results.Json(
                new { error = "Plan configuration is invalid", planId = assignment.PlanId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 3. Check billing period rollover (read-only in precheck to avoid write-side effects)
        var usagePolicy = await usagePolicyStore.GetAsync(db);
        var currentDateUtc = DateTime.UtcNow;
        var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(currentDateUtc, usagePolicy.BillingCycleStartDay);
        var newBillingPeriod =
            assignment.CurrentPeriodStart != expectedPeriodStart;
        var effectiveUsage = newBillingPeriod ? 0 : assignment.CurrentPeriodUsage;
        var effectiveDeploymentUsage = newBillingPeriod
            ? new Dictionary<string, long>()
            : assignment.DeploymentUsage;

        // 4. Check quota (with per-deployment support)
        if (!plan.RollUpAllDeployments)
        {
            var deploymentId = context.Request.Query["deploymentId"].ToString();
            if (!string.IsNullOrEmpty(deploymentId) && plan.DeploymentQuotas.TryGetValue(deploymentId, out var deploymentLimit))
            {
                var deploymentUsage = effectiveDeploymentUsage.GetValueOrDefault(deploymentId, 0);
                if (deploymentUsage >= deploymentLimit && !plan.AllowOverbilling)
                {
                    return Results.Json(
                        new { error = "Per-deployment quota exceeded", deploymentId, usage = deploymentUsage, limit = deploymentLimit },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }
            }
        }
        else
        {
            if (effectiveUsage >= plan.MonthlyTokenQuota && !plan.AllowOverbilling)
            {
                return Results.Json(
                    new { error = "Quota exceeded", usage = effectiveUsage, limit = plan.MonthlyTokenQuota },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // 5. Check rate limits (atomic increment — acts as reservation)
        var now = DateTimeOffset.UtcNow;
        var minuteWindow = now.ToUnixTimeSeconds() / 60;
        long currentRpm = 0;
        long currentTpm = 0;

        if (plan.RequestsPerMinuteLimit > 0)
        {
            var rpmKey = RedisKeys.RateLimitRpm(clientAppId, tenantId, minuteWindow);
            currentRpm = await db.StringIncrementAsync(rpmKey);
            if (currentRpm == 1)
                await db.KeyExpireAsync(rpmKey, TimeSpan.FromSeconds(120));
            if (currentRpm > plan.RequestsPerMinuteLimit)
            {
                return Results.Json(
                    new { error = "Rate limit exceeded — requests per minute", limit = plan.RequestsPerMinuteLimit, current = currentRpm },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        if (plan.TokensPerMinuteLimit > 0)
        {
            var tpmKey = RedisKeys.RateLimitTpm(clientAppId, tenantId, minuteWindow);
            currentTpm = (long)(await db.StringGetAsync(tpmKey));
            if (currentTpm >= plan.TokensPerMinuteLimit)
            {
                return Results.Json(
                    new { error = "Rate limit exceeded — tokens per minute", limit = plan.TokensPerMinuteLimit, current = currentTpm },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // 6. Check deployment access control
        var accessDeploymentId = context.Request.Query["deploymentId"].ToString();
        if (!string.IsNullOrEmpty(accessDeploymentId))
        {
            // Client override takes precedence over plan
            var effectiveAllowedDeployments = (assignment.AllowedDeployments is { Count: > 0 })
                ? assignment.AllowedDeployments
                : plan.AllowedDeployments;

            if (effectiveAllowedDeployments is { Count: > 0 } &&
                !effectiveAllowedDeployments.Contains(accessDeploymentId, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { error = "Deployment not allowed", deploymentId = accessDeploymentId, allowedDeployments = effectiveAllowedDeployments },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        return Results.Ok(new { status = "authorized", clientAppId, tenantId, plan = plan.Name, usage = effectiveUsage, limit = plan.MonthlyTokenQuota, currentRpm, rpmLimit = plan.RequestsPerMinuteLimit, currentTpm, tpmLimit = plan.TokensPerMinuteLimit });
    }
}
