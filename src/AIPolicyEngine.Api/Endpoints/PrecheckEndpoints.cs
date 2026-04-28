using System.Collections.Concurrent;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Endpoints;

public static class PrecheckEndpoints
{
    // In-memory cache for routing policies — avoids Redis on every hot-path request.
    // Key = policyId, Value = (policy, lastRefreshed).
    private static readonly ConcurrentDictionary<string, (ModelRoutingPolicy Policy, DateTime Loaded)> RoutingPolicyCache = new();
    private static readonly TimeSpan RoutingPolicyCacheTtl = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapPrecheckEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/precheck/{clientAppId}/{tenantId}", Precheck)
            .WithName("Precheck")
            .WithDescription("Pre-authorize a client+tenant request — checks plan, quota, rate limits, and model routing")
            .RequireAuthorization("ApimPolicy");

        routes.MapPost("/api/content-check/{clientAppId}/{tenantId}", ContentCheck)
            .WithName("ContentCheck")
            .WithDescription("DLP content check — evaluates prompt against Purview policy before forwarding to LLM")
            .RequireAuthorization("ApimPolicy");

        return routes;
    }

    private static async Task<IResult> Precheck(
        string clientAppId,
        string tenantId,
        HttpContext context,
        IRepository<ClientPlanAssignment> clientRepo,
        IRepository<PlanData> planRepo,
        IRepository<ModelRoutingPolicy> routingPolicyRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
        ILogger<PlanData> logger)
    {
        // 1. Checkclient assignment exists (reads from Redis cache via CachedRepository)
        var clientId = $"{clientAppId}:{tenantId}";
        var assignment = await clientRepo.GetAsync(clientId);
        if (assignment is null)
        {
            return Results.Json(
                new { error = "Client not authorized — no plan assigned", clientAppId, tenantId },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // 2. Check plan exists (reads from Redis cache via CachedRepository)
        var plan = await planRepo.GetAsync(assignment.PlanId);
        if (plan is null)
        {
            return Results.Json(
                new { error = "Plan configuration not found", planId = assignment.PlanId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // 3. Routing evaluation — determine effective deployment
        var requestedDeploymentId = context.Request.Query["deploymentId"].ToString();
        string? routedDeploymentId = null;
        string? routingPolicyId = null;

        var effectivePolicyId = assignment.ModelRoutingPolicyOverride ?? plan.ModelRoutingPolicyId;
        if (!string.IsNullOrEmpty(effectivePolicyId) && !string.IsNullOrEmpty(requestedDeploymentId))
        {
            routingPolicyId = effectivePolicyId;
            var policy = await GetCachedRoutingPolicy(effectivePolicyId, routingPolicyRepo);

            var routingResult = RoutingEvaluator.Evaluate(requestedDeploymentId, policy);

            if (!routingResult.IsAllowed)
            {
                return Results.Json(
                    new { error = "Deployment denied by routing policy", deploymentId = requestedDeploymentId, routingPolicyId },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (routingResult.WasRouted)
                routedDeploymentId = routingResult.DeploymentId;
        }

        // effectiveDeployment = routed target (if routing changed it), otherwise what was requested
        var effectiveDeployment = routedDeploymentId ?? requestedDeploymentId;

        // 4. Check billing period rollover (read-only in precheck to avoid write-side effects)
        var usagePolicy = await usagePolicyStore.GetAsync();
        var currentDateUtc = DateTime.UtcNow;
        var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(currentDateUtc, usagePolicy.BillingCycleStartDay);
        var newBillingPeriod =
            assignment.CurrentPeriodStart != expectedPeriodStart;
        var effectiveUsage = newBillingPeriod ? 0 : assignment.CurrentPeriodUsage;
        var effectiveDeploymentUsage = newBillingPeriod
            ? new Dictionary<string, long>()
            : assignment.DeploymentUsage;

        // 5. Check quota (with per-deployment support) — uses effective (routed) deployment
        if (!plan.RollUpAllDeployments)
        {
            if (!string.IsNullOrEmpty(effectiveDeployment) && plan.DeploymentQuotas.TryGetValue(effectiveDeployment, out var deploymentLimit))
            {
                var deploymentUsage = effectiveDeploymentUsage.GetValueOrDefault(effectiveDeployment, 0);
                if (deploymentUsage >= deploymentLimit && !plan.AllowOverbilling)
                {
                    return Results.Json(
                        new { error = "Per-deployment quota exceeded", deploymentId = effectiveDeployment, usage = deploymentUsage, limit = deploymentLimit },
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

        // Check multiplier request quota
        if (plan.UseMultiplierBilling && plan.MonthlyRequestQuota > 0)
        {
            var effectiveRequests = newBillingPeriod ? 0 : assignment.CurrentPeriodRequests;
            if (effectiveRequests >= plan.MonthlyRequestQuota && !plan.AllowOverbilling)
            {
                return Results.Json(
                    new { error = "Request quota exceeded", usage = effectiveRequests, limit = plan.MonthlyRequestQuota },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // 6. Check rate limits— deployment-scoped keys use the ROUTED deployment
        var db = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var minuteWindow = now.ToUnixTimeSeconds() / 60;
        long currentRpm = 0;
        long currentTpm = 0;

        if (plan.RequestsPerMinuteLimit > 0)
        {
            // Use deployment-scoped key if we have a deployment, else fall back to legacy key
            var rpmKey = !string.IsNullOrEmpty(effectiveDeployment)
                ? RedisKeys.RateLimitRpm(clientAppId, tenantId, effectiveDeployment, minuteWindow)
                : RedisKeys.RateLimitRpm(clientAppId, tenantId, minuteWindow);
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
            var tpmKey = !string.IsNullOrEmpty(effectiveDeployment)
                ? RedisKeys.RateLimitTpm(clientAppId, tenantId, effectiveDeployment, minuteWindow)
                : RedisKeys.RateLimitTpm(clientAppId, tenantId, minuteWindow);
            currentTpm = (long)(await db.StringGetAsync(tpmKey));
            if (currentTpm >= plan.TokensPerMinuteLimit)
            {
                return Results.Json(
                    new { error = "Rate limit exceeded — tokens per minute", limit = plan.TokensPerMinuteLimit, current = currentTpm },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // 7. Check deployment access control — runs on the ROUTED deployment
        if (!string.IsNullOrEmpty(effectiveDeployment))
        {
            var effectiveAllowedDeployments = (assignment.AllowedDeployments is { Count: > 0 })
                ? assignment.AllowedDeployments
                : plan.AllowedDeployments;

            if (effectiveAllowedDeployments is { Count: > 0 } &&
                !effectiveAllowedDeployments.Contains(effectiveDeployment, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { error = "Deployment not allowed", deploymentId = effectiveDeployment, allowedDeployments = effectiveAllowedDeployments },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // 8. Return enriched response with routing metadata
        return Results.Ok(new
        {
            status = "authorized",
            clientAppId,
            tenantId,
            plan = plan.Name,
            usage = effectiveUsage,
            limit = plan.MonthlyTokenQuota,
            currentRpm,
            rpmLimit = plan.RequestsPerMinuteLimit,
            currentTpm,
            tpmLimit = plan.TokensPerMinuteLimit,
            routedDeployment = routedDeploymentId,
            requestedDeployment = requestedDeploymentId,
            routingPolicyId
        });
    }

    /// <summary>
    /// Loads a routing policy from in-memory cache, falling back to the repository.
    /// Cache entries are refreshed every 30 seconds.
    /// </summary>
    private static async Task<ModelRoutingPolicy?> GetCachedRoutingPolicy(
        string policyId, IRepository<ModelRoutingPolicy> routingPolicyRepo)
    {
        if (RoutingPolicyCache.TryGetValue(policyId, out var cached) &&
            DateTime.UtcNow - cached.Loaded < RoutingPolicyCacheTtl)
        {
            return cached.Policy;
        }

        var policy = await routingPolicyRepo.GetAsync(policyId);
        if (policy is not null)
        {
            RoutingPolicyCache[policyId] = (policy, DateTime.UtcNow);
        }
        else
        {
            RoutingPolicyCache.TryRemove(policyId, out _);
        }

        return policy;
    }

    private static async Task<IResult> ContentCheck(
        string clientAppId,
        string tenantId,
        HttpContext context,
        IRepository<ClientPlanAssignment> clientRepo,
        IPurviewAuditService purviewAuditService,
        ILogger<PlanData> logger,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var clientId= $"{clientAppId}:{tenantId}";
        var assignment = await clientRepo.GetAsync(clientId);

        string clientDisplayName = clientAppId;
        if (assignment is null)
        {
            logger.LogWarning(
                "Content check — client not found (using fallback): ClientAppId={ClientAppId} TenantId={TenantId}",
                clientAppId, tenantId);
        }
        else
        {
            clientDisplayName = assignment.DisplayName ?? clientAppId;
        }

        var result = await purviewAuditService.CheckContentAsync(content, tenantId, clientDisplayName, cancellationToken);

        if (result.IsBlocked)
        {
            return Results.Json(
                new { blocked = true, message = result.BlockMessage },
                statusCode: 451);
        }

        return Results.Ok(new { blocked = false });
    }

    /// <summary>Invalidates the in-memory routing policy cache (for testing).</summary>
    internal static void ClearRoutingPolicyCache() => RoutingPolicyCache.Clear();
}
