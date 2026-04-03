using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// CRUD endpoints for model routing policies.
/// Validates all deployment references against DeploymentDiscoveryService.
/// Backed by Cosmos (source of truth) + Redis cache via IRepository.
/// </summary>
public static class RoutingPolicyEndpoints
{
    public static IEndpointRouteBuilder MapRoutingPolicyEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/routing-policies", ListPolicies)
            .WithName("ListRoutingPolicies")
            .WithDescription("List all model routing policies")
            .RequireAuthorization("AdminPolicy")
            .Produces<RoutingPoliciesResponse>();

        routes.MapPost("/api/routing-policies", CreatePolicy)
            .WithName("CreateRoutingPolicy")
            .WithDescription("Create a new model routing policy")
            .RequireAuthorization("AdminPolicy")
            .Produces<ModelRoutingPolicy>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapGet("/api/routing-policies/{policyId}", GetPolicy)
            .WithName("GetRoutingPolicy")
            .WithDescription("Get a specific routing policy by ID")
            .RequireAuthorization("AdminPolicy")
            .Produces<ModelRoutingPolicy>()
            .Produces(StatusCodes.Status404NotFound);

        routes.MapPut("/api/routing-policies/{policyId}", UpdatePolicy)
            .WithName("UpdateRoutingPolicy")
            .WithDescription("Update an existing routing policy")
            .RequireAuthorization("AdminPolicy")
            .Produces<ModelRoutingPolicy>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/routing-policies/{policyId}", DeletePolicy)
            .WithName("DeleteRoutingPolicy")
            .WithDescription("Delete a routing policy (fails if in use by plans)")
            .RequireAuthorization("AdminPolicy")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListPolicies(
        IRepository<ModelRoutingPolicy> routingRepo,
        ILogger<RoutingPoliciesResponse> logger)
    {
        try
        {
            var policies = await routingRepo.GetAllAsync();
            logger.LogInformation("Fetched {Count} routing policies", policies.Count);
            return Results.Json(new RoutingPoliciesResponse { Policies = policies }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching routing policies");
            return Results.Json(new { error = "Failed to fetch routing policies" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> CreatePolicy(
        RoutingPolicyCreateRequest body,
        IRepository<ModelRoutingPolicy> routingRepo,
        IDeploymentDiscoveryService deploymentService,
        ILogger<ModelRoutingPolicy> logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                logger.LogWarning("Create routing policy rejected: name is required");
                return Results.BadRequest("Routing policy name is required");
            }

            var validationError = await ValidateDeployments(body.Rules, body.FallbackDeployment, deploymentService, logger);
            if (validationError is not null)
                return Results.BadRequest(validationError);

            var id = Guid.NewGuid().ToString("N")[..8];
            var now = DateTime.UtcNow;

            var policy = new ModelRoutingPolicy
            {
                Id = id,
                Name = body.Name.Trim(),
                Description = body.Description ?? string.Empty,
                Rules = body.Rules ?? [],
                DefaultBehavior = body.DefaultBehavior ?? RoutingBehavior.Passthrough,
                FallbackDeployment = body.FallbackDeployment,
                CreatedAt = now,
                UpdatedAt = now
            };

            var persisted = await routingRepo.UpsertAsync(policy);

            logger.LogInformation("Routing policy created: Id={PolicyId}, Name={Name}", id, policy.Name);
            return Results.Json(persisted, JsonConfig.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating routing policy");
            return Results.Json(new { error = "Failed to create routing policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetPolicy(
        string policyId,
        IRepository<ModelRoutingPolicy> routingRepo,
        ILogger<ModelRoutingPolicy> logger)
    {
        try
        {
            var policy = await routingRepo.GetAsync(policyId);
            if (policy is null)
                return Results.NotFound(new { error = $"Routing policy '{policyId}' not found" });

            return Results.Json(policy, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching routing policy {PolicyId}", policyId);
            return Results.Json(new { error = "Failed to fetch routing policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpdatePolicy(
        string policyId,
        RoutingPolicyUpdateRequest body,
        IRepository<ModelRoutingPolicy> routingRepo,
        IDeploymentDiscoveryService deploymentService,
        ILogger<ModelRoutingPolicy> logger)
    {
        try
        {
            var policy = await routingRepo.GetAsync(policyId);
            if (policy is null)
                return Results.NotFound(new { error = $"Routing policy '{policyId}' not found" });

            // Validate deployments in the rules being updated
            var rulesToValidate = body.Rules ?? policy.Rules;
            var fallbackToValidate = body.FallbackDeployment ?? policy.FallbackDeployment;
            var validationError = await ValidateDeployments(rulesToValidate, fallbackToValidate, deploymentService, logger);
            if (validationError is not null)
                return Results.BadRequest(validationError);

            if (body.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(body.Name))
                    return Results.BadRequest("Routing policy name cannot be empty");
                policy.Name = body.Name.Trim();
            }
            if (body.Description is not null) policy.Description = body.Description;
            if (body.Rules is not null) policy.Rules = body.Rules;
            if (body.DefaultBehavior.HasValue) policy.DefaultBehavior = body.DefaultBehavior.Value;
            if (body.FallbackDeployment is not null) policy.FallbackDeployment = body.FallbackDeployment;
            policy.UpdatedAt = DateTime.UtcNow;

            var persisted = await routingRepo.UpsertAsync(policy);

            logger.LogInformation("Routing policy updated: Id={PolicyId}", policyId);
            return Results.Json(persisted, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating routing policy {PolicyId}", policyId);
            return Results.Json(new { error = "Failed to update routing policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeletePolicy(
        string policyId,
        IRepository<ModelRoutingPolicy> routingRepo,
        IRepository<PlanData> planRepo,
        IRepository<ClientPlanAssignment> clientRepo,
        ILogger<ModelRoutingPolicy> logger)
    {
        try
        {
            // Check if any plans reference this routing policy
            var plans = await planRepo.GetAllAsync();
            var plansUsingPolicy = plans
                .Where(p => string.Equals(p.ModelRoutingPolicyId, policyId, StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToList();

            // Check if any client assignments override with this policy
            var clients = await clientRepo.GetAllAsync();
            var clientsUsingPolicy = clients
                .Where(c => string.Equals(c.ModelRoutingPolicyOverride, policyId, StringComparison.Ordinal))
                .Select(c => $"{c.ClientAppId}:{c.TenantId}")
                .ToList();

            if (plansUsingPolicy.Count > 0 || clientsUsingPolicy.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = $"Routing policy '{policyId}' is in use and cannot be deleted",
                    plans = plansUsingPolicy,
                    clients = clientsUsingPolicy
                });
            }

            var deleted = await routingRepo.DeleteAsync(policyId);
            if (!deleted)
                return Results.NotFound(new { error = $"Routing policy '{policyId}' not found" });

            logger.LogInformation("Routing policy deleted: Id={PolicyId}", policyId);
            return Results.Ok(new { message = "Routing policy deleted successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting routing policy {PolicyId}", policyId);
            return Results.Json(new { error = "Failed to delete routing policy" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Validates that all RoutedDeployment values and the optional FallbackDeployment
    /// exist in the known Foundry deployments.
    /// </summary>
    private static async Task<object?> ValidateDeployments(
        List<RouteRule>? rules,
        string? fallbackDeployment,
        IDeploymentDiscoveryService deploymentService,
        ILogger logger)
    {
        if ((rules is null || rules.Count == 0) && string.IsNullOrWhiteSpace(fallbackDeployment))
            return null;

        var knownDeployments = await deploymentService.GetDeploymentsAsync();
        var knownIds = knownDeployments.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // If discovery returned no deployments, reject the request (cannot validate routing rules)
        if (knownIds.Count == 0)
        {
            logger.LogWarning("Routing policy rejected: Foundry returned 0 deployments — cannot validate");
            return new
            {
                error = "No deployments available from Foundry. Cannot validate routing rules."
            };
        }

        var invalidDeployments = new List<string>();

        if (rules is not null)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.RequestedDeployment))
                {
                    invalidDeployments.Add("<empty RequestedDeployment>");
                }
                else if (!knownIds.Contains(rule.RequestedDeployment))
                {
                    invalidDeployments.Add(rule.RequestedDeployment);
                }

                if (!string.IsNullOrWhiteSpace(rule.RoutedDeployment) && !knownIds.Contains(rule.RoutedDeployment))
                    invalidDeployments.Add(rule.RoutedDeployment);
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackDeployment) && !knownIds.Contains(fallbackDeployment))
            invalidDeployments.Add(fallbackDeployment);

        if (invalidDeployments.Count > 0)
        {
            logger.LogWarning(
                "Routing policy rejected: invalid deployments {Invalid}. Available: {Available}",
                invalidDeployments, knownIds.ToList());
            return new
            {
                error = "One or more deployments are not known Foundry deployments",
                invalidDeployments = invalidDeployments.Distinct().ToList(),
                availableDeployments = knownIds.ToList()
            };
        }

        return null;
    }
}

// ── Request/Response DTOs ────────────────────────────────────────────

public sealed class RoutingPoliciesResponse
{
    public List<ModelRoutingPolicy> Policies { get; set; } = [];
}

public sealed class RoutingPolicyCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<RouteRule>? Rules { get; set; }
    public RoutingBehavior? DefaultBehavior { get; set; }
    public string? FallbackDeployment { get; set; }
}

public sealed class RoutingPolicyUpdateRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<RouteRule>? Rules { get; set; }
    public RoutingBehavior? DefaultBehavior { get; set; }
    public string? FallbackDeployment { get; set; }
}
