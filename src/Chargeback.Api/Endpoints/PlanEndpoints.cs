using System.Text.Json;
using Chargeback.Api.Models;
using Chargeback.Api.Services;
using StackExchange.Redis;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// Plan and client assignment management endpoints for the billing system.
/// </summary>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder routes)
    {
        // Plan CRUD
        routes.MapGet("/api/plans", GetPlans)
            .WithName("GetPlans")
            .WithDescription("List all billing plans")
            .RequireAuthorization()
            .Produces<PlansResponse>();

        routes.MapPost("/api/plans", CreatePlan)
            .WithName("CreatePlan")
            .WithDescription("Create a new billing plan")
            .RequireAuthorization("AdminPolicy")
            .Produces<PlanData>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapPut("/api/plans/{planId}", UpdatePlan)
            .WithName("UpdatePlan")
            .WithDescription("Update an existing billing plan")
            .RequireAuthorization("AdminPolicy")
            .Produces<PlanData>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/plans/{planId}", DeletePlan)
            .WithName("DeletePlan")
            .WithDescription("Delete a billing plan")
            .RequireAuthorization("AdminPolicy")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        // Client assignment CRUD
        routes.MapGet("/api/clients", GetClients)
            .WithName("GetClients")
            .WithDescription("List all client plan assignments with current usage")
            .RequireAuthorization()
            .Produces<ClientsResponse>();

        routes.MapPut("/api/clients/{clientAppId}/{tenantId}", AssignClient)
            .WithName("AssignClient")
            .WithDescription("Assign or reassign a customer (client+tenant) to a billing plan")
            .RequireAuthorization("AdminPolicy")
            .Produces<ClientPlanAssignment>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/clients/{clientAppId}/{tenantId}", DeleteClient)
            .WithName("DeleteClient")
            .WithDescription("Remove a customer plan assignment")
            .RequireAuthorization("AdminPolicy")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    // ── Plan CRUD ───────────────────────────────────────────────────────

    private static async Task<IResult> GetPlans(
        IConnectionMultiplexer redis,
        ILogger<PlansResponse> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var keys = redis.KeysFromAllServers(RedisKeys.PlanPrefix);

            logger.LogInformation("Fetched {KeyCount} plan keys from Redis", keys.Length);

            var plans = new List<PlanData>();
            foreach (var key in keys)
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;

                try
                {
                    var plan = JsonSerializer.Deserialize<PlanData>((string)value!, JsonConfig.Default);
                    if (plan is not null)
                        plans.Add(plan);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize plan for key {Key}", key);
                }
            }

            return Results.Json(new PlansResponse { Plans = plans }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching plans");
            return Results.Json(new { error = "Failed to fetch plans" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> CreatePlan(
        PlanCreateRequest body,
        IConnectionMultiplexer redis,
        ILogger<PlanData> logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest("Plan name is required");

            var normalizedName = NormalizePlanName(body.Name);

            var id = Guid.NewGuid().ToString("N")[..8];
            var now = DateTime.UtcNow;
            var db = redis.GetDatabase();

            if (await PlanNameExistsAsync(db, redis, normalizedName, logger))
                return Results.Conflict(new { error = $"Plan name '{normalizedName}' already exists" });

            var plan = new PlanData
            {
                Id = id,
                Name = normalizedName,
                MonthlyRate = body.MonthlyRate,
                MonthlyTokenQuota = body.MonthlyTokenQuota,
                TokensPerMinuteLimit = body.TokensPerMinuteLimit,
                RequestsPerMinuteLimit = body.RequestsPerMinuteLimit,
                AllowOverbilling = body.AllowOverbilling,
                CostPerMillionTokens = body.CostPerMillionTokens,
                RollUpAllDeployments = body.RollUpAllDeployments ?? true,
                DeploymentQuotas = body.DeploymentQuotas ?? new(),
                AllowedDeployments = body.AllowedDeployments ?? [],
                CreatedAt = now,
                UpdatedAt = now
            };

            var cacheKey = RedisKeys.Plan(id);
            var cacheValue = JsonSerializer.Serialize(plan, JsonConfig.Default);
            await db.StringSetAsync(cacheKey, cacheValue);

            logger.LogInformation("Plan created: Id={PlanId}, Name={Name}", id, plan.Name);
            return Results.Json(plan, JsonConfig.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating plan");
            return Results.Json(new { error = "Failed to create plan" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpdatePlan(
        string planId,
        PlanUpdateRequest body,
        IConnectionMultiplexer redis,
        ILogger<PlanData> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var cacheKey = RedisKeys.Plan(planId);
            var existing = await db.StringGetAsync(cacheKey);

            if (!existing.HasValue)
                return Results.NotFound(new { error = $"Plan '{planId}' not found" });

            var plan = JsonSerializer.Deserialize<PlanData>((string)existing!, JsonConfig.Default);
            if (plan is null)
                return Results.NotFound(new { error = $"Plan '{planId}' not found" });

            if (body.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(body.Name))
                    return Results.BadRequest("Plan name is required");

                var normalizedName = NormalizePlanName(body.Name);
                if (await PlanNameExistsAsync(db, redis, normalizedName, logger, excludedPlanId: planId))
                    return Results.Conflict(new { error = $"Plan name '{normalizedName}' already exists" });

                plan.Name = normalizedName;
            }
            if (body.MonthlyRate.HasValue) plan.MonthlyRate = body.MonthlyRate.Value;
            if (body.MonthlyTokenQuota.HasValue) plan.MonthlyTokenQuota = body.MonthlyTokenQuota.Value;
            if (body.TokensPerMinuteLimit.HasValue) plan.TokensPerMinuteLimit = body.TokensPerMinuteLimit.Value;
            if (body.RequestsPerMinuteLimit.HasValue) plan.RequestsPerMinuteLimit = body.RequestsPerMinuteLimit.Value;
            if (body.AllowOverbilling.HasValue) plan.AllowOverbilling = body.AllowOverbilling.Value;
            if (body.CostPerMillionTokens.HasValue) plan.CostPerMillionTokens = body.CostPerMillionTokens.Value;
            if (body.RollUpAllDeployments.HasValue) plan.RollUpAllDeployments = body.RollUpAllDeployments.Value;
            if (body.DeploymentQuotas is not null) plan.DeploymentQuotas = body.DeploymentQuotas;
            if (body.AllowedDeployments is not null) plan.AllowedDeployments = body.AllowedDeployments;
            plan.UpdatedAt = DateTime.UtcNow;

            var cacheValue = JsonSerializer.Serialize(plan, JsonConfig.Default);
            await db.StringSetAsync(cacheKey, cacheValue);

            logger.LogInformation("Plan updated: Id={PlanId}", planId);
            return Results.Json(plan, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating plan {PlanId}", planId);
            return Results.Json(new { error = "Failed to update plan" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeletePlan(
        string planId,
        IConnectionMultiplexer redis,
        ILogger<PlanData> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var cacheKey = RedisKeys.Plan(planId);

            var assignedClientIds = await GetAssignedClientIdsAsync(db, redis, planId, logger);
            if (assignedClientIds.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = $"Plan '{planId}' is assigned to one or more clients and cannot be deleted",
                    clientAppIds = assignedClientIds
                });
            }

            var deleted = await db.KeyDeleteAsync(cacheKey);

            if (!deleted)
                return Results.NotFound(new { error = $"Plan '{planId}' not found" });

            logger.LogInformation("Plan deleted: Id={PlanId}", planId);
            return Results.Ok(new { message = "Plan deleted successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting plan {PlanId}", planId);
            return Results.Json(new { error = "Failed to delete plan" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ── Client Assignment CRUD ──────────────────────────────────────────

    private static async Task<IResult> GetClients(
        IConnectionMultiplexer redis,
        IUsagePolicyStore usagePolicyStore,
        ILogger<ClientsResponse> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var usagePolicy = await usagePolicyStore.GetAsync(db);
            var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(DateTime.UtcNow, usagePolicy.BillingCycleStartDay);
            var keys = redis.KeysFromAllServers(RedisKeys.ClientPrefix);

            logger.LogInformation("Fetched {KeyCount} client keys from Redis", keys.Length);

            var clients = new List<ClientPlanAssignment>();
            foreach (var key in keys)
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;

                try
                {
                    var client = JsonSerializer.Deserialize<ClientPlanAssignment>((string)value!, JsonConfig.Default);
                    if (client is null) continue;

                    // Skip stale keys from pre-migration format (missing tenantId)
                    if (string.IsNullOrWhiteSpace(client.TenantId)) continue;

                    if (client.CurrentPeriodStart != expectedPeriodStart)
                    {
                        client.CurrentPeriodUsage = 0;
                        client.OverbilledTokens = 0;
                        client.DeploymentUsage = new();
                        client.CurrentPeriodStart = expectedPeriodStart;
                    }

                    var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                    var rpmVal = await db.StringGetAsync(RedisKeys.RateLimitRpm(client.ClientAppId, client.TenantId, minuteWindow));
                    var tpmVal = await db.StringGetAsync(RedisKeys.RateLimitTpm(client.ClientAppId, client.TenantId, minuteWindow));
                    client.CurrentRpm = rpmVal.HasValue ? (long)rpmVal : 0;
                    client.CurrentTpm = tpmVal.HasValue ? (long)tpmVal : 0;

                    clients.Add(client);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Failed to deserialize client for key {Key}", key);
                }
            }

            return Results.Json(new ClientsResponse { Clients = clients }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching clients");
            return Results.Json(new { error = "Failed to fetch clients" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> AssignClient(
        string clientAppId,
        string tenantId,
        ClientAssignRequest body,
        IConnectionMultiplexer redis,
        IUsagePolicyStore usagePolicyStore,
        ILogger<ClientPlanAssignment> logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientAppId))
                return Results.BadRequest("clientAppId is required");

            if (string.IsNullOrWhiteSpace(tenantId))
                return Results.BadRequest("tenantId is required");

            if (string.IsNullOrWhiteSpace(body.PlanId))
                return Results.BadRequest("planId is required");

            var db = redis.GetDatabase();
            var usagePolicy = await usagePolicyStore.GetAsync(db);

            var planKey = RedisKeys.Plan(body.PlanId);
            if (!await db.KeyExistsAsync(planKey))
                return Results.BadRequest($"Plan '{body.PlanId}' does not exist");

            var currentUsage = await ComputeUsage(db, redis, clientAppId, tenantId, logger);

            var assignment = new ClientPlanAssignment
            {
                ClientAppId = clientAppId,
                TenantId = tenantId,
                PlanId = body.PlanId,
                DisplayName = body.DisplayName ?? $"{clientAppId}/{tenantId}",
                CurrentPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(DateTime.UtcNow, usagePolicy.BillingCycleStartDay),
                CurrentPeriodUsage = currentUsage,
                OverbilledTokens = 0,
                AllowedDeployments = body.AllowedDeployments ?? [],
                LastUpdated = DateTime.UtcNow
            };

            var cacheKey = RedisKeys.Client(clientAppId, tenantId);
            var cacheValue = JsonSerializer.Serialize(assignment, JsonConfig.Default);
            await db.StringSetAsync(cacheKey, cacheValue);

            logger.LogInformation(
                "Client assigned: ClientAppId={ClientAppId}, TenantId={TenantId}, PlanId={PlanId}, Usage={Usage}",
                clientAppId, tenantId, body.PlanId, currentUsage);

            return Results.Json(assignment, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error assigning customer {ClientAppId}/{TenantId}", clientAppId, tenantId);
            return Results.Json(new { error = "Failed to assign client" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeleteClient(
        string clientAppId,
        string tenantId,
        IConnectionMultiplexer redis,
        ILogger<ClientPlanAssignment> logger)
    {
        try
        {
            var db = redis.GetDatabase();
            var cacheKey = RedisKeys.Client(clientAppId, tenantId);
            var deleted = await db.KeyDeleteAsync(cacheKey);

            if (!deleted)
                return Results.NotFound(new { error = $"Customer '{clientAppId}/{tenantId}' not found" });

            logger.LogInformation("Client deleted: ClientAppId={ClientAppId}, TenantId={TenantId}", clientAppId, tenantId);
            return Results.Ok(new { message = "Client assignment deleted successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting customer {ClientAppId}/{TenantId}", clientAppId, tenantId);
            return Results.Json(new { error = "Failed to delete client" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string NormalizePlanName(string name) => name.Trim();

    private static async Task<bool> PlanNameExistsAsync(
        IDatabase db,
        IConnectionMultiplexer redis,
        string normalizedName,
        ILogger logger,
        string? excludedPlanId = null)
    {
        var planKeys = redis.KeysFromAllServers(RedisKeys.PlanPrefix);
        foreach (var planKey in planKeys)
        {
            var planValue = await db.StringGetAsync(planKey);
            if (!planValue.HasValue)
                continue;

            try
            {
                var existingPlan = JsonSerializer.Deserialize<PlanData>((string)planValue!, JsonConfig.Default);
                if (existingPlan is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(excludedPlanId) &&
                    string.Equals(existingPlan.Id, excludedPlanId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var existingName = NormalizePlanName(existingPlan.Name);
                if (string.Equals(existingName, normalizedName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize plan data while validating plan name uniqueness for key {Key}", planKey);
            }
        }

        return false;
    }

    private static async Task<List<string>> GetAssignedClientIdsAsync(
        IDatabase db,
        IConnectionMultiplexer redis,
        string planId,
        ILogger logger)
    {
        var assignedClientIds = new List<string>();
        var clientKeys = redis.KeysFromAllServers(RedisKeys.ClientPrefix);

        foreach (var clientKey in clientKeys)
        {
            var clientValue = await db.StringGetAsync(clientKey);
            if (!clientValue.HasValue)
                continue;

            try
            {
                var assignment = JsonSerializer.Deserialize<ClientPlanAssignment>((string)clientValue!, JsonConfig.Default);
                if (assignment is null)
                    continue;

                if (string.Equals(assignment.PlanId, planId, StringComparison.Ordinal))
                    assignedClientIds.Add(assignment.ClientAppId);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize client assignment while validating plan delete guard for key {Key}", clientKey);
            }
        }

        return assignedClientIds;
    }

    private static async Task<long> ComputeUsage(IDatabase db, IConnectionMultiplexer redis, string clientAppId, string tenantId, ILogger logger)
    {
        long usage = 0;
        var logKeys = redis.KeysFromAllServers(RedisKeys.CustomerLogPattern(clientAppId, tenantId));

        foreach (var logKey in logKeys)
        {
            var logValue = await db.StringGetAsync(logKey);
            if (!logValue.HasValue) continue;

            try
            {
                var cached = JsonSerializer.Deserialize<CachedLogData>((string)logValue!, JsonConfig.Default);
                if (cached is not null)
                    usage += cached.TotalTokens;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize log data for key {Key}", logKey);
            }
        }

        return usage;
    }
}
