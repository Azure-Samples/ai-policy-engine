using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// Plan and client assignment management endpoints for the billing system.
/// Backed by Cosmos (source of truth) + Redis cache via IRepository.
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
        IRepository<PlanData> planRepo,
        ILogger<PlansResponse> logger)
    {
        try
        {
            var plans = await planRepo.GetAllAsync();
            logger.LogInformation("Fetched {Count} plans", plans.Count);
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
        IRepository<PlanData> planRepo,
        ILogger<PlanData> logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest("Plan name is required");

            var normalizedName = NormalizePlanName(body.Name);

            if (await PlanNameExistsAsync(planRepo, normalizedName))
                return Results.Conflict(new { error = $"Plan name '{normalizedName}' already exists" });

            var id = Guid.NewGuid().ToString("N")[..8];
            var now = DateTime.UtcNow;

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
                ModelRoutingPolicyId = body.ModelRoutingPolicyId,
                MonthlyRequestQuota = body.MonthlyRequestQuota ?? 0,
                OverageRatePerRequest = body.OverageRatePerRequest ?? 0,
                UseMultiplierBilling = body.UseMultiplierBilling ?? false,
                CreatedAt = now,
                UpdatedAt = now
            };

            var persisted = await planRepo.UpsertAsync(plan);

            logger.LogInformation("Plan created: Id={PlanId}, Name={Name}", id, plan.Name);
            return Results.Json(persisted, JsonConfig.Default, statusCode: StatusCodes.Status201Created);
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
        IRepository<PlanData> planRepo,
        ILogger<PlanData> logger)
    {
        try
        {
            var plan = await planRepo.GetAsync(planId);
            if (plan is null)
                return Results.NotFound(new { error = $"Plan '{planId}' not found" });

            if (body.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(body.Name))
                    return Results.BadRequest("Plan name is required");

                var normalizedName = NormalizePlanName(body.Name);
                if (await PlanNameExistsAsync(planRepo, normalizedName, excludedPlanId: planId))
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
            if (body.ModelRoutingPolicyId is not null) plan.ModelRoutingPolicyId = body.ModelRoutingPolicyId.Length > 0 ? body.ModelRoutingPolicyId : null;
            if (body.MonthlyRequestQuota.HasValue) plan.MonthlyRequestQuota = body.MonthlyRequestQuota.Value;
            if (body.OverageRatePerRequest.HasValue) plan.OverageRatePerRequest = body.OverageRatePerRequest.Value;
            if (body.UseMultiplierBilling.HasValue) plan.UseMultiplierBilling = body.UseMultiplierBilling.Value;
            plan.UpdatedAt = DateTime.UtcNow;

            var persisted = await planRepo.UpsertAsync(plan);

            logger.LogInformation("Plan updated: Id={PlanId}", planId);
            return Results.Json(persisted, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating plan {PlanId}", planId);
            return Results.Json(new { error = "Failed to update plan" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeletePlan(
        string planId,
        IRepository<PlanData> planRepo,
        IRepository<ClientPlanAssignment> clientRepo,
        ILogger<PlanData> logger)
    {
        try
        {
            var assignedClientIds = await GetAssignedClientIdsAsync(clientRepo, planId);
            if (assignedClientIds.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = $"Plan '{planId}' is assigned to one or more clients and cannot be deleted",
                    clientAppIds = assignedClientIds
                });
            }

            var deleted = await planRepo.DeleteAsync(planId);

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
        IRepository<ClientPlanAssignment> clientRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
        ILogger<ClientsResponse> logger)
    {
        try
        {
            var usagePolicy = await usagePolicyStore.GetAsync();
            var expectedPeriodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(DateTime.UtcNow, usagePolicy.BillingCycleStartDay);
            var allClients = await clientRepo.GetAllAsync();

            var db = redis.GetDatabase();
            var clients = new List<ClientPlanAssignment>();

            foreach (var client in allClients)
            {
                // Skip stale keys from pre-migration format (missing tenantId)
                if (string.IsNullOrWhiteSpace(client.TenantId)) continue;

                if (client.CurrentPeriodStart != expectedPeriodStart)
                {
                    client.CurrentPeriodUsage = 0;
                    client.OverbilledTokens = 0;
                    client.DeploymentUsage = new();
                    client.CurrentPeriodRequests = 0;
                    client.OverbilledRequests = 0;
                    client.RequestsByTier = new();
                    client.CurrentPeriodStart = expectedPeriodStart;
                }

                // Rate limit data is ephemeral — stays Redis-direct
                var minuteWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                var rpmVal = await db.StringGetAsync(RedisKeys.RateLimitRpm(client.ClientAppId, client.TenantId, minuteWindow));
                var tpmVal = await db.StringGetAsync(RedisKeys.RateLimitTpm(client.ClientAppId, client.TenantId, minuteWindow));
                client.CurrentRpm = rpmVal.HasValue ? (long)rpmVal : 0;
                client.CurrentTpm = tpmVal.HasValue ? (long)tpmVal : 0;

                clients.Add(client);
            }

            logger.LogInformation("Fetched {Count} clients", clients.Count);
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
        IRepository<PlanData> planRepo,
        IRepository<ClientPlanAssignment> clientRepo,
        IUsagePolicyStore usagePolicyStore,
        IConnectionMultiplexer redis,
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

            var plan = await planRepo.GetAsync(body.PlanId);
            if (plan is null)
                return Results.BadRequest($"Plan '{body.PlanId}' does not exist");

            var usagePolicy = await usagePolicyStore.GetAsync();
            var currentUsage = await ComputeUsage(redis, clientAppId, tenantId, logger);

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

            var persisted = await clientRepo.UpsertAsync(assignment);

            logger.LogInformation(
                "Client assigned: ClientAppId={ClientAppId}, TenantId={TenantId}, PlanId={PlanId}, Usage={Usage}",
                clientAppId, tenantId, body.PlanId, currentUsage);

            return Results.Json(persisted, JsonConfig.Default);
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
        IRepository<ClientPlanAssignment> clientRepo,
        ILogger<ClientPlanAssignment> logger)
    {
        try
        {
            var clientId = $"{clientAppId}:{tenantId}";
            var deleted = await clientRepo.DeleteAsync(clientId);

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
        IRepository<PlanData> planRepo,
        string normalizedName,
        string? excludedPlanId = null)
    {
        var plans = await planRepo.GetAllAsync();
        return plans.Any(p =>
            (!string.IsNullOrWhiteSpace(excludedPlanId)
                ? !string.Equals(p.Id, excludedPlanId, StringComparison.OrdinalIgnoreCase)
                : true) &&
            string.Equals(NormalizePlanName(p.Name), normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<string>> GetAssignedClientIdsAsync(
        IRepository<ClientPlanAssignment> clientRepo,
        string planId)
    {
        var clients = await clientRepo.GetAllAsync();
        return clients
            .Where(c => string.Equals(c.PlanId, planId, StringComparison.Ordinal))
            .Select(c => c.ClientAppId)
            .ToList();
    }

    private static async Task<long> ComputeUsage(IConnectionMultiplexer redis, string clientAppId, string tenantId, ILogger logger)
    {
        // Log data is ephemeral — stays Redis-direct
        long usage = 0;
        var db = redis.GetDatabase();
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
