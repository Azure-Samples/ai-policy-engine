using System.Collections.Concurrent;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.AccessProfiles;
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
        IAccessProfileResolver accessProfileResolver,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
        ILogger<PlanData> logger)
    {
        var requestedDeploymentId = context.Request.Query["deploymentId"].ToString();
        var apiId = NormalizeOptional(context.Request.Query["apiId"].ToString());
        var operationId = NormalizeOptional(context.Request.Query["operationId"].ToString());
        _ = context.Request.Query["subscriptionId"].ToString();

        if (apiId is null)
        {
            return await LegacyPrecheck(
                clientAppId,
                tenantId,
                requestedDeploymentId,
                clientRepo,
                planRepo,
                routingPolicyRepo,
                usagePolicyStore,
                redis);
        }

        var resolved = await accessProfileResolver.ResolveAsync(clientAppId, tenantId, apiId, operationId);
        var assignment = await clientRepo.GetAsync($"{clientAppId}:{tenantId}");

        if (resolved is { Blocked: true })
        {
            return Results.Json(
                new
                {
                    error = "Access blocked by access profile",
                    clientAppId,
                    tenantId,
                    apiId,
                    operationId,
                    accessProfileId = resolved.AccessProfileId,
                    deniedBy = "access-profile-blocked"
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (resolved is null)
        {
            if (assignment is null)
            {
                return Results.Json(
                    new
                    {
                        error = "Client not authorized — no access profile or plan assigned",
                        clientAppId,
                        tenantId,
                        apiId,
                        operationId,
                        deniedBy = "no-profile-no-assignment"
                    },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var fallbackPlan = await planRepo.GetAsync(assignment.PlanId);
            if (fallbackPlan is null)
            {
                logger.LogError("Plan not found during AAA fallback precheck: {PlanId} for client {ClientAppId}/{TenantId}", assignment.PlanId, clientAppId, tenantId);
                return Results.Json(
                    new { error = "Plan configuration not found", planId = assignment.PlanId },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var fallbackAllowedDeployments = assignment.AllowedDeployments is { Count: > 0 }
                ? assignment.AllowedDeployments
                : fallbackPlan.AllowedDeployments;

            return await EvaluatePrecheck(
                clientAppId,
                tenantId,
                requestedDeploymentId,
                assignment,
                fallbackPlan,
                assignment.ModelRoutingPolicyOverride ?? fallbackPlan.ModelRoutingPolicyId,
                fallbackAllowedDeployments,
                routingPolicyRepo,
                usagePolicyStore,
                redis,
                includeAccessProfileMetadata: true,
                resolvedPlanId: assignment.PlanId,
                accessProfileId: null,
                apiId: apiId,
                operationId: operationId);
        }

        if (assignment is null)
        {
            return Results.Json(
                new
                {
                    error = "Client not authorized — no plan assigned",
                    clientAppId,
                    tenantId,
                    apiId,
                    operationId,
                    accessProfileId = resolved.AccessProfileId,
                    deniedBy = "no-client-assignment"
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var plan = await planRepo.GetAsync(resolved.PlanId);
        if (plan is null)
        {
            logger.LogError("Plan not found during AAA precheck: {PlanId} for client {ClientAppId}/{TenantId}", resolved.PlanId, clientAppId, tenantId);
            return Results.Json(
                new { error = "Plan configuration not found", planId = resolved.PlanId, accessProfileId = resolved.AccessProfileId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var effectiveAllowedDeployments = resolved.AllowedDeployments is { Count: > 0 }
            ? resolved.AllowedDeployments
            : plan.AllowedDeployments;

        return await EvaluatePrecheck(
            clientAppId,
            tenantId,
            requestedDeploymentId,
            assignment,
            plan,
            resolved.RoutingPolicyId ?? plan.ModelRoutingPolicyId,
            effectiveAllowedDeployments,
            routingPolicyRepo,
            usagePolicyStore,
            redis,
            includeAccessProfileMetadata: true,
            resolvedPlanId: resolved.PlanId,
            accessProfileId: resolved.AccessProfileId,
            apiId: apiId,
            operationId: operationId);
    }

    private static async Task<IResult> LegacyPrecheck(
        string clientAppId,
        string tenantId,
        string requestedDeploymentId,
        IRepository<ClientPlanAssignment> clientRepo,
        IRepository<PlanData> planRepo,
        IRepository<ModelRoutingPolicy> routingPolicyRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis)
    {
        var clientId = $"{clientAppId}:{tenantId}";
        var assignment = await clientRepo.GetAsync(clientId);
        if (assignment is null)
        {
            return Results.Json(
                new { error = "Client not authorized — no plan assigned", clientAppId, tenantId },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var plan = await planRepo.GetAsync(assignment.PlanId);
        if (plan is null)
        {
            return Results.Json(
                new { error = "Plan configuration not found", planId = assignment.PlanId },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var effectiveAllowedDeployments = assignment.AllowedDeployments is { Count: > 0 }
            ? assignment.AllowedDeployments
            : plan.AllowedDeployments;

        return await EvaluatePrecheck(
            clientAppId,
            tenantId,
            requestedDeploymentId,
            assignment,
            plan,
            assignment.ModelRoutingPolicyOverride ?? plan.ModelRoutingPolicyId,
            effectiveAllowedDeployments,
            routingPolicyRepo,
            usagePolicyStore,
            redis,
            includeAccessProfileMetadata: false,
            resolvedPlanId: assignment.PlanId);
    }

    private static async Task<IResult> EvaluatePrecheck(
        string clientAppId,
        string tenantId,
        string requestedDeploymentId,
        ClientPlanAssignment assignment,
        PlanData plan,
        string? effectivePolicyId,
        IReadOnlyCollection<string> effectiveAllowedDeployments,
        IRepository<ModelRoutingPolicy> routingPolicyRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
        bool includeAccessProfileMetadata,
        string resolvedPlanId,
        string? accessProfileId = null,
        string? apiId = null,
        string? operationId = null)
    {
        string? routedDeploymentId = null;
        string? routingPolicyId = null;

        if (!string.IsNullOrEmpty(effectivePolicyId) && !string.IsNullOrEmpty(requestedDeploymentId))
        {
            routingPolicyId = effectivePolicyId;
            var policy = await GetCachedRoutingPolicy(effectivePolicyId, routingPolicyRepo);
            var routingResult = RoutingEvaluator.Evaluate(requestedDeploymentId, policy);

            if (!routingResult.IsAllowed)
            {
                return includeAccessProfileMetadata
                    ? Results.Json(
                        new
                        {
                            error = "Deployment denied by routing policy",
                            deploymentId = requestedDeploymentId,
                            routingPolicyId,
                            planId = resolvedPlanId,
                            accessProfileId,
                            apiId,
                            operationId,
                            deniedBy = "routing-denied"
                        },
                        statusCode: StatusCodes.Status403Forbidden)
                    : Results.Json(
                        new { error = "Deployment denied by routing policy", deploymentId = requestedDeploymentId, routingPolicyId },
                        statusCode: StatusCodes.Status403Forbidden);
            }

            if (routingResult.WasRouted)
                routedDeploymentId = routingResult.DeploymentId;
        }

        var effectiveDeployment = routedDeploymentId ?? requestedDeploymentId;

        var usagePolicy = await usagePolicyStore.GetAsync();
        var currentDateUtc = DateTime.UtcNow;
        var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(currentDateUtc, usagePolicy.BillingCycleStartDay);
        var newBillingPeriod = assignment.CurrentPeriodStart != expectedPeriodStart;
        var effectiveUsage = newBillingPeriod ? 0 : assignment.CurrentPeriodUsage;
        var effectiveDeploymentUsage = newBillingPeriod ? new Dictionary<string, long>() : assignment.DeploymentUsage;

        if (!plan.RollUpAllDeployments)
        {
            if (!string.IsNullOrEmpty(effectiveDeployment) && plan.DeploymentQuotas.TryGetValue(effectiveDeployment, out var deploymentLimit))
            {
                var deploymentUsage = effectiveDeploymentUsage.GetValueOrDefault(effectiveDeployment, 0);
                if (deploymentUsage >= deploymentLimit && !plan.AllowOverbilling)
                {
                    return includeAccessProfileMetadata
                        ? Results.Json(
                            new
                            {
                                error = "Per-deployment quota exceeded",
                                deploymentId = effectiveDeployment,
                                usage = deploymentUsage,
                                limit = deploymentLimit,
                                planId = resolvedPlanId,
                                accessProfileId,
                                apiId,
                                operationId,
                                deniedBy = "quota-exceeded"
                            },
                            statusCode: StatusCodes.Status429TooManyRequests)
                        : Results.Json(
                            new { error = "Per-deployment quota exceeded", deploymentId = effectiveDeployment, usage = deploymentUsage, limit = deploymentLimit },
                            statusCode: StatusCodes.Status429TooManyRequests);
                }
            }
        }
        else if (effectiveUsage >= plan.MonthlyTokenQuota && !plan.AllowOverbilling)
        {
            return includeAccessProfileMetadata
                ? Results.Json(
                    new
                    {
                        error = "Quota exceeded",
                        usage = effectiveUsage,
                        limit = plan.MonthlyTokenQuota,
                        planId = resolvedPlanId,
                        accessProfileId,
                        apiId,
                        operationId,
                        deniedBy = "quota-exceeded"
                    },
                    statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Json(
                    new { error = "Quota exceeded", usage = effectiveUsage, limit = plan.MonthlyTokenQuota },
                    statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (plan.UseMultiplierBilling && plan.MonthlyRequestQuota > 0)
        {
            var effectiveRequests = newBillingPeriod ? 0 : assignment.CurrentPeriodRequests;
            if (effectiveRequests >= plan.MonthlyRequestQuota && !plan.AllowOverbilling)
            {
                return includeAccessProfileMetadata
                    ? Results.Json(
                        new
                        {
                            error = "Request quota exceeded",
                            usage = effectiveRequests,
                            limit = plan.MonthlyRequestQuota,
                            planId = resolvedPlanId,
                            accessProfileId,
                            apiId,
                            operationId,
                            deniedBy = "request-quota-exceeded"
                        },
                        statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(
                        new { error = "Request quota exceeded", usage = effectiveRequests, limit = plan.MonthlyRequestQuota },
                        statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        var db = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var minuteWindow = now.ToUnixTimeSeconds() / 60;
        long currentRpm = 0;
        long currentTpm = 0;

        if (plan.RequestsPerMinuteLimit > 0)
        {
            var rpmKey = !string.IsNullOrEmpty(effectiveDeployment)
                ? RedisKeys.RateLimitRpm(clientAppId, tenantId, effectiveDeployment, minuteWindow)
                : RedisKeys.RateLimitRpm(clientAppId, tenantId, minuteWindow);
            currentRpm = await db.StringIncrementAsync(rpmKey);
            if (currentRpm == 1)
                await db.KeyExpireAsync(rpmKey, TimeSpan.FromSeconds(120));
            if (currentRpm > plan.RequestsPerMinuteLimit)
            {
                return includeAccessProfileMetadata
                    ? Results.Json(
                        new
                        {
                            error = "Rate limit exceeded — requests per minute",
                            limit = plan.RequestsPerMinuteLimit,
                            current = currentRpm,
                            planId = resolvedPlanId,
                            accessProfileId,
                            apiId,
                            operationId,
                            deniedBy = "rpm-exceeded"
                        },
                        statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(
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
                return includeAccessProfileMetadata
                    ? Results.Json(
                        new
                        {
                            error = "Rate limit exceeded — tokens per minute",
                            limit = plan.TokensPerMinuteLimit,
                            current = currentTpm,
                            planId = resolvedPlanId,
                            accessProfileId,
                            apiId,
                            operationId,
                            deniedBy = "tpm-exceeded"
                        },
                        statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(
                        new { error = "Rate limit exceeded — tokens per minute", limit = plan.TokensPerMinuteLimit, current = currentTpm },
                        statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        if (!string.IsNullOrEmpty(effectiveDeployment) &&
            effectiveAllowedDeployments.Count > 0 &&
            !effectiveAllowedDeployments.Contains(effectiveDeployment, StringComparer.OrdinalIgnoreCase))
        {
            return includeAccessProfileMetadata
                ? Results.Json(
                    new
                    {
                        error = "Deployment not allowed",
                        deploymentId = effectiveDeployment,
                        allowedDeployments = effectiveAllowedDeployments,
                        planId = resolvedPlanId,
                        accessProfileId,
                        apiId,
                        operationId,
                        deniedBy = "deployment-denied"
                    },
                    statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(
                    new { error = "Deployment not allowed", deploymentId = effectiveDeployment, allowedDeployments = effectiveAllowedDeployments },
                    statusCode: StatusCodes.Status403Forbidden);
        }

        return includeAccessProfileMetadata
            ? Results.Ok(new
            {
                status = "authorized",
                clientAppId,
                tenantId,
                plan = plan.Name,
                planId = resolvedPlanId,
                accessProfileId,
                allowedDeployments = effectiveAllowedDeployments,
                usage = effectiveUsage,
                limit = plan.MonthlyTokenQuota,
                currentRpm,
                rpmLimit = plan.RequestsPerMinuteLimit,
                currentTpm,
                tpmLimit = plan.TokensPerMinuteLimit,
                routedDeployment = routedDeploymentId,
                requestedDeployment = requestedDeploymentId,
                routingPolicyId
            })
            : Results.Ok(new
            {
                status = "authorized",
                clientAppId,
                tenantId,
                plan = plan.Name,
                planId = resolvedPlanId,
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

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Invalidates the in-memory routing policy cache (for testing).</summary>
    internal static void ClearRoutingPolicyCache() => RoutingPolicyCache.Clear();
}
