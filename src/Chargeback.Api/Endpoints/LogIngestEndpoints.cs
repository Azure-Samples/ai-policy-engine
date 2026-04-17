using System.Text.Json;
using System.Threading.Channels;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using StackExchange.Redis;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// Log ingestion endpoint called by the APIM outbound policy.
/// Replaces the Python Azure Function (process_logs).
/// </summary>
public static class LogIngestEndpoints
{
    // TTL must cover the full read-compute-write cycle including Cosmos latency.
    // If the lock expires mid-operation, concurrent requests can read stale data.
    private static readonly TimeSpan ClientUpdateLockTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClientUpdateLockRetryDelay = TimeSpan.FromMilliseconds(25);
    private const int ClientUpdateLockMaxAttempts = 40;

    public static RouteGroupBuilder MapLogIngestEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api");

        group.MapPost("/log", IngestLog)
            .WithName("IngestLog")
            .WithDescription("Receives log data from APIM outbound policy and stores in Redis")
            .RequireAuthorization("ApimPolicy")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status500InternalServerError);

        return group;
    }

    private static async Task<IResult> IngestLog(
        HttpRequest request,
        IConnectionMultiplexer redis,
        IRepository<ClientPlanAssignment> clientRepo,
        IRepository<PlanData> planRepo,
        IUsagePolicyStore usagePolicyStore,
        IChargebackCalculator calculator,
        ChargebackMetrics metrics,
        IPurviewAuditService purviewAudit,
        Channel<AuditLogItem>auditChannel,
        ILogger<LogIngestRequest> logger)
    {
        LogIngestRequest? ingestRequest;
        try
        {
            ingestRequest = await request.ReadFromJsonAsync<LogIngestRequest>(JsonConfig.Default);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid request body");
            return Results.BadRequest("Invalid request body");
        }

        if (ingestRequest is null)
        {
            return Results.BadRequest("Empty request body");
        }

        if (string.IsNullOrWhiteSpace(ingestRequest.TenantId) ||
            string.IsNullOrWhiteSpace(ingestRequest.ClientAppId) ||
            string.IsNullOrWhiteSpace(ingestRequest.DeploymentId))
        {
            logger.LogError("Missing required fields: tenantId={TenantId}, clientAppId={ClientAppId}, deploymentId={DeploymentId}",
                ingestRequest.TenantId, ingestRequest.ClientAppId, ingestRequest.DeploymentId);
            return Results.BadRequest("Missing required fields (tenantId, clientAppId, deploymentId)");
        }

        var responseBody = ingestRequest.ResponseBody;
        var model = responseBody?.Model ?? "unknown";
        var objectType = responseBody?.Object ?? "unknown";
        var usage = responseBody?.Usage;

        try
        {
            var db = redis.GetDatabase();
            var usagePolicy = await usagePolicyStore.GetAsync();
            var logCacheTtl = TimeSpan.FromDays(usagePolicy.AggregatedLogRetentionDays);
            var traceCacheTtl = TimeSpan.FromDays(usagePolicy.TraceRetentionDays);
            var lockToken = (RedisValue)Guid.NewGuid().ToString("N");
            if (!await TryAcquireClientUpdateLock(db, ingestRequest.ClientAppId, ingestRequest.TenantId, lockToken, logger))
            {
                return Results.Json(
                    new { error = "Client usage update is busy, retry request" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            CachedLogData? logData = null;
            ClientPlanAssignment? clientAssignment = null;
            decimal? effectiveRequestCost = null;
            decimal? multiplier = null;
            string? tierName = null;
            decimal? multiplierOverageCost = null;

            try
            {
                // --- 1. Client Authorization Check ---
                var clientId = $"{ingestRequest.ClientAppId}:{ingestRequest.TenantId}";
                clientAssignment = await clientRepo.GetAsync(clientId);
                if (clientAssignment is null)
                {
                    logger.LogWarning("Unauthorized client: {ClientAppId}/{TenantId} — no plan assigned", ingestRequest.ClientAppId, ingestRequest.TenantId);
                    return Results.Json(new { error = "Client not authorized — no plan assigned" }, statusCode: StatusCodes.Status401Unauthorized);
                }

                // --- 2. Plan Lookup ---
                var plan = await planRepo.GetAsync(clientAssignment.PlanId);
                if (plan is null)
                {
                    logger.LogError("Plan not found: {PlanId} for client {ClientAppId}", clientAssignment.PlanId, ingestRequest.ClientAppId);
                    return Results.Json(new { error = "Plan configuration not found" }, statusCode: StatusCodes.Status500InternalServerError);
                }

                // --- 3. Update rate limit meters (outbound — record actual token usage) ---
                var now = DateTimeOffset.UtcNow;
                var minuteWindow = now.ToUnixTimeSeconds() / 60;
                var totalTokensInRequest = usage?.TotalTokens ?? 0;

                await UpdateTpmCounter(db, plan, ingestRequest.ClientAppId, ingestRequest.TenantId, minuteWindow, totalTokensInRequest, logger);

                // --- 4. Quota Check + Overbilling ---
                ResetBillingPeriodIfNeeded(clientAssignment, usagePolicy.BillingCycleStartDay, DateTime.UtcNow);

                var newUsage = clientAssignment.CurrentPeriodUsage + totalTokensInRequest;
                var isOverQuota = newUsage > plan.MonthlyTokenQuota;

                if (isOverQuota)
                {
                    logger.LogWarning("Over quota: {ClientAppId}, usage {Usage}/{Limit}, overbilling={AllowOverbilling}",
                        ingestRequest.ClientAppId, newUsage, plan.MonthlyTokenQuota, plan.AllowOverbilling);
                }

                // --- Build log data ---
                logData = new CachedLogData
                {
                    TenantId = ingestRequest.TenantId,
                    ClientAppId = ingestRequest.ClientAppId,
                    Audience = ingestRequest.Audience,
                    DeploymentId = ingestRequest.DeploymentId,
                    Model = model,
                    ObjectType = objectType,
                    PromptTokens = usage?.PromptTokens ?? 0,
                    CompletionTokens = usage?.CompletionTokens ?? 0,
                    TotalTokens = usage?.TotalTokens ?? 0,
                    ImageTokens = usage?.ImageTokens ?? 0
                };

                // --- 5. Calculate costs ---
                logData.CostToUs = calculator.CalculateCost(logData);
                logData.IsOverbilled = isOverQuota;
                logData.CostToCustomer = isOverQuota ? calculator.CalculateCustomerCost(logData, plan) : 0m;
                var requestCostToUs = logData.CostToUs;
                var requestCostToCustomer = logData.CostToCustomer;
                var requestIsOverbilled = logData.IsOverbilled;

                // --- 5b. Multiplier billing ---
                effectiveRequestCost = null;
                multiplier = null;
                tierName = null;
                multiplierOverageCost = null;

                if (plan.UseMultiplierBilling)
                {
                    effectiveRequestCost = calculator.CalculateEffectiveRequestCost(logData);
                    multiplier = calculator.GetMultiplier(logData.DeploymentId, logData.Model);
                    tierName = calculator.GetTierName(logData.DeploymentId, logData.Model);

                    // Update request counters
                    clientAssignment.CurrentPeriodRequests += effectiveRequestCost.Value;
                    if (!string.IsNullOrEmpty(tierName))
                    {
                        if (!clientAssignment.RequestsByTier.ContainsKey(tierName))
                            clientAssignment.RequestsByTier[tierName] = 0m;
                        clientAssignment.RequestsByTier[tierName] += effectiveRequestCost.Value;
                    }

                    // Check request-based overage
                    if (plan.MonthlyRequestQuota > 0 && clientAssignment.CurrentPeriodRequests > plan.MonthlyRequestQuota)
                    {
                        var overageAmount = clientAssignment.CurrentPeriodRequests - plan.MonthlyRequestQuota;
                        clientAssignment.OverbilledRequests = overageAmount;
                    }

                    multiplierOverageCost = calculator.CalculateMultiplierOverageCost(
                        effectiveRequestCost.Value,
                        clientAssignment.CurrentPeriodRequests - effectiveRequestCost.Value,
                        plan);
                }

                // Update client assignment usage
                clientAssignment.CurrentPeriodUsage = newUsage;
                if (isOverQuota)
                    clientAssignment.OverbilledTokens += totalTokensInRequest;

                if (!clientAssignment.DeploymentUsage.ContainsKey(ingestRequest.DeploymentId))
                    clientAssignment.DeploymentUsage[ingestRequest.DeploymentId] = 0;
                clientAssignment.DeploymentUsage[ingestRequest.DeploymentId] += totalTokensInRequest;

                clientAssignment.LastUpdated = DateTime.UtcNow;

                // Persist client assignment via repository (Cosmos → Redis cache)
                // Extend the lock TTL before the Cosmos write to prevent expiry during I/O
                await db.LockExtendAsync(
                    RedisKeys.ClientUpdateLock(ingestRequest.ClientAppId, ingestRequest.TenantId),
                    lockToken, ClientUpdateLockTtl);
                await clientRepo.UpsertAsync(clientAssignment);

                // --- Aggregate into log cache (ephemeral — stays Redis-direct) ---
                var cacheKey = RedisKeys.LogEntry(logData.ClientAppId, logData.TenantId, logData.DeploymentId);
                var existingValue = await db.StringGetAsync(cacheKey);

                if (existingValue.HasValue)
                {
                    var existingData = JsonSerializer.Deserialize<CachedLogData>((string)existingValue!, JsonConfig.Default);
                    if (existingData is not null)
                    {
                        logData.PromptTokens += existingData.PromptTokens;
                        logData.CompletionTokens += existingData.CompletionTokens;
                        logData.TotalTokens += existingData.TotalTokens;
                        logData.ImageTokens += existingData.ImageTokens;
                        logData.CostToUs += existingData.CostToUs;
                        logData.CostToCustomer += existingData.CostToCustomer;
                        logData.IsOverbilled = logData.IsOverbilled || existingData.IsOverbilled;
                    }
                }

                var cacheValue = JsonSerializer.Serialize(logData, JsonConfig.Default);
                await db.StringSetAsync(cacheKey, cacheValue, logCacheTtl);

                logger.LogInformation(
                    "Log data cached: Key={CacheKey}, TenantId={TenantId}, ClientAppId={ClientAppId}, DeploymentId={DeploymentId}, Model={Model}, TotalTokens={TotalTokens}",
                    cacheKey, logData.TenantId, logData.ClientAppId, logData.DeploymentId, logData.Model, logData.TotalTokens);

                // Record trace for client detail page (ephemeral — stays Redis-direct)
                var trace = new TraceRecord
                {
                    Timestamp = DateTime.UtcNow,
                    DeploymentId = ingestRequest.DeploymentId,
                    Model = model,
                    PromptTokens = usage?.PromptTokens ?? 0,
                    CompletionTokens = usage?.CompletionTokens ?? 0,
                    TotalTokens = usage?.TotalTokens ?? 0,
                    CostToUs = requestCostToUs.ToString("F4"),
                    CostToCustomer = requestCostToCustomer.ToString("F4"),
                    IsOverbilled = requestIsOverbilled,
                    StatusCode = 200
                };
                var traceJson = JsonSerializer.Serialize(trace, JsonConfig.Default);
                var traceKey = RedisKeys.Traces(ingestRequest.ClientAppId, ingestRequest.TenantId);
                await db.ListLeftPushAsync(traceKey, traceJson);
                await db.ListTrimAsync(traceKey, 0, 99);
                await db.KeyExpireAsync(traceKey, traceCacheTtl);

                logger.LogInformation(
                    "Usage trace exported: TenantId={TenantId}, ClientAppId={ClientAppId}, DeploymentId={DeploymentId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalTokens={TotalTokens}, CostToUs={CostToUs}, CostToCustomer={CostToCustomer}, IsOverbilled={IsOverbilled}, StatusCode={StatusCode}",
                    ingestRequest.TenantId,
                    ingestRequest.ClientAppId,
                    ingestRequest.DeploymentId,
                    model,
                    usage?.PromptTokens ?? 0,
                    usage?.CompletionTokens ?? 0,
                    usage?.TotalTokens ?? 0,
                    requestCostToUs,
                    requestCostToCustomer,
                    requestIsOverbilled,
                    trace.StatusCode);
            }
            finally
            {
                await ReleaseClientUpdateLock(db, ingestRequest.ClientAppId, ingestRequest.TenantId, lockToken, logger);
            }

            // Emit custom metrics
            metrics.RecordTokensProcessed(usage?.TotalTokens ?? 0, ingestRequest.TenantId, ingestRequest.ClientAppId, model, ingestRequest.DeploymentId);
            metrics.RecordRequest(ingestRequest.TenantId, ingestRequest.ClientAppId, model);

            // Emit Purview audit event (fire-and-forget via background channel)
            await purviewAudit.EmitAuditEventAsync(ingestRequest, clientAssignment?.DisplayName ?? ingestRequest.ClientAppId);

            // Enqueue audit item for durable Cosmos DB persistence (non-blocking)
            auditChannel.Writer.TryWrite(new AuditLogItem
            {
                ClientAppId = ingestRequest.ClientAppId,
                DisplayName = clientAssignment?.DisplayName ?? ingestRequest.ClientAppId,
                TenantId = ingestRequest.TenantId,
                Audience = ingestRequest.Audience,
                DeploymentId = ingestRequest.DeploymentId,
                Model = model,
                PromptTokens = usage?.PromptTokens ?? 0,
                CompletionTokens = usage?.CompletionTokens ?? 0,
                TotalTokens = usage?.TotalTokens ?? 0,
                ImageTokens = usage?.ImageTokens ?? 0,
                CostToUs = logData?.CostToUs.ToString("F4") ?? "0.0000",
                CostToCustomer = logData?.CostToCustomer.ToString("F4") ?? "0.0000",
                IsOverbilled = logData?.IsOverbilled ?? false,
                StatusCode = 200,
                Timestamp = DateTime.UtcNow,
                RequestedDeploymentId = ingestRequest.DeploymentId,
                RoutingPolicyId = ingestRequest.RoutingPolicyId,
                Multiplier = multiplier,
                EffectiveRequestCost = effectiveRequestCost,
                TierName = tierName,
                MultiplierOverageCost = multiplierOverageCost,
                CorrelationId = ingestRequest.CorrelationId
            });

            return Results.Ok("Log data processed and stored successfully");
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "Failed to interact with Redis");
            return Results.Json(new { error = "Failed to interact with Redis" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<bool> TryAcquireClientUpdateLock(
        IDatabase db,
        string clientAppId,
        string tenantId,
        RedisValue lockToken,
        ILogger logger)
    {
        var lockKey = RedisKeys.ClientUpdateLock(clientAppId, tenantId);

        for (var attempt = 0; attempt < ClientUpdateLockMaxAttempts; attempt++)
        {
            if (await db.LockTakeAsync(lockKey, lockToken, ClientUpdateLockTtl))
                return true;

            await Task.Delay(ClientUpdateLockRetryDelay);
        }

        logger.LogWarning("Failed to acquire client usage lock for {ClientAppId}/{TenantId}", clientAppId, tenantId);
        return false;
    }

    private static async Task ReleaseClientUpdateLock(
        IDatabase db,
        string clientAppId,
        string tenantId,
        RedisValue lockToken,
        ILogger logger)
    {
        try
        {
            var lockKey = RedisKeys.ClientUpdateLock(clientAppId, tenantId);
            await db.LockReleaseAsync(lockKey, lockToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Failed to release client usage lock for {ClientAppId}/{TenantId}", clientAppId, tenantId);
        }
    }

    private static async Task UpdateTpmCounter(
        IDatabase db, PlanData plan, string clientAppId, string tenantId, long minuteWindow, long totalTokens, ILogger logger)
    {
        if (plan.TokensPerMinuteLimit > 0 && totalTokens > 0)
        {
            var tpmKey = RedisKeys.RateLimitTpm(clientAppId, tenantId, minuteWindow);
            var currentTpm = await db.StringIncrementAsync(tpmKey, totalTokens);
            if (currentTpm == totalTokens)
                await db.KeyExpireAsync(tpmKey, TimeSpan.FromSeconds(120));

            logger.LogDebug("TPM updated: {ClientAppId}/{TenantId} = {Current}/{Limit}",
                clientAppId, tenantId, currentTpm, plan.TokensPerMinuteLimit);
        }
    }

    private static void ResetBillingPeriodIfNeeded(ClientPlanAssignment assignment, int billingCycleStartDay, DateTime nowUtc)
    {
        var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(nowUtc, billingCycleStartDay);
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
    }
}
