using System.Security.Claims;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.AccessProfiles;

namespace AIPolicyEngine.Api.Endpoints;

public static class AccessProfileEndpoints
{
    public static IEndpointRouteBuilder MapAccessProfileEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/access-profiles", ListProfiles)
            .WithName("ListAccessProfiles")
            .WithDescription("List access profiles")
            .RequireAuthorization("AdminPolicy")
            .Produces<AccessProfilesResponse>();

        routes.MapGet("/api/access-profiles/{profileId}", GetProfile)
            .WithName("GetAccessProfile")
            .WithDescription("Get a specific access profile")
            .RequireAuthorization("AdminPolicy")
            .Produces<AccessProfile>()
            .Produces(StatusCodes.Status404NotFound);

        routes.MapPost("/api/access-profiles", CreateProfile)
            .WithName("CreateAccessProfile")
            .WithDescription("Create an access profile")
            .RequireAuthorization("AdminPolicy")
            .Produces<AccessProfile>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapPut("/api/access-profiles/{profileId}", UpdateProfile)
            .WithName("UpdateAccessProfile")
            .WithDescription("Update an access profile")
            .RequireAuthorization("AdminPolicy")
            .Produces<AccessProfile>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        routes.MapDelete("/api/access-profiles/{profileId}", DeleteProfile)
            .WithName("DeleteAccessProfile")
            .WithDescription("Delete an access profile")
            .RequireAuthorization("AdminPolicy")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        routes.MapPost("/api/access-profiles/bulk", BulkCreateProfiles)
            .WithName("BulkCreateAccessProfiles")
            .WithDescription("Create access profiles in bulk")
            .RequireAuthorization("AdminPolicy")
            .Produces<BulkAccessProfilesResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListProfiles(
        string? clientAppId,
        string? tenantId,
        string? apiId,
        IAccessProfileRepository repository,
        ILogger<AccessProfile> logger)
    {
        try
        {
            var profiles = await repository.ListAsync(clientAppId, tenantId, apiId);
            logger.LogInformation("Fetched {Count} access profiles", profiles.Count);
            return Results.Json(new AccessProfilesResponse { Profiles = profiles }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching access profiles");
            return Results.Json(new { error = "Failed to fetch access profiles" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetProfile(
        string profileId,
        IAccessProfileRepository repository,
        ILogger<AccessProfile> logger)
    {
        try
        {
            var profile = await repository.GetAsync(profileId);
            if (profile is null)
                return Results.NotFound(new { error = $"Access profile '{profileId}' not found" });

            return Results.Json(profile, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching access profile {ProfileId}", profileId);
            return Results.Json(new { error = "Failed to fetch access profile" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> CreateProfile(
        AccessProfileCreateRequest body,
        ClaimsPrincipal user,
        IAccessProfileRepository repository,
        IRepository<PlanData> planRepository,
        IRepository<ModelRoutingPolicy> routingPolicyRepository,
        ILogger<AccessProfile> logger)
    {
        try
        {
            var buildResult = await BuildProfileAsync(body, repository, planRepository, routingPolicyRepository, user, logger);
            if (buildResult.ErrorResult is not null)
                return buildResult.ErrorResult;

            var persisted = await repository.UpsertAsync(buildResult.Profile!);
            logger.LogInformation("Access profile created: {ProfileId}", persisted.Id);
            return Results.Json(persisted, JsonConfig.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating access profile");
            return Results.Json(new { error = "Failed to create access profile" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpdateProfile(
        string profileId,
        AccessProfileUpdateRequest body,
        IAccessProfileRepository repository,
        IRepository<PlanData> planRepository,
        IRepository<ModelRoutingPolicy> routingPolicyRepository,
        ILogger<AccessProfile> logger)
    {
        try
        {
            var profile = await repository.GetAsync(profileId);
            if (profile is null)
                return Results.NotFound(new { error = $"Access profile '{profileId}' not found" });

            if (body.PlanId is not null)
            {
                if (string.IsNullOrWhiteSpace(body.PlanId))
                    return Results.BadRequest("planId cannot be empty");

                var planExists = await planRepository.GetAsync(body.PlanId.Trim());
                if (planExists is null)
                    return Results.BadRequest($"Plan '{body.PlanId.Trim()}' not found");

                profile.PlanId = body.PlanId.Trim();
            }

            if (body.RoutingPolicyId is not null)
            {
                var normalizedRoutingPolicyId = NormalizeOptional(body.RoutingPolicyId);
                if (normalizedRoutingPolicyId is not null)
                {
                    var routingPolicy = await routingPolicyRepository.GetAsync(normalizedRoutingPolicyId);
                    if (routingPolicy is null)
                        return Results.BadRequest($"Routing policy '{normalizedRoutingPolicyId}' not found");
                }

                profile.RoutingPolicyId = normalizedRoutingPolicyId;
            }

            if (body.AllowedDeployments is not null)
                profile.AllowedDeployments = NormalizeAllowedDeployments(body.AllowedDeployments);

            if (body.Enabled.HasValue)
                profile.Enabled = body.Enabled.Value;

            profile.UpdatedAt = DateTime.UtcNow;
            var persisted = await repository.UpsertAsync(profile);
            logger.LogInformation("Access profile updated: {ProfileId}", persisted.Id);
            return Results.Json(persisted, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating access profile {ProfileId}", profileId);
            return Results.Json(new { error = "Failed to update access profile" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeleteProfile(
        string profileId,
        IAccessProfileRepository repository,
        ILogger<AccessProfile> logger)
    {
        try
        {
            var deleted = await repository.DeleteAsync(profileId);
            if (!deleted)
                return Results.NotFound(new { error = $"Access profile '{profileId}' not found" });

            logger.LogInformation("Access profile deleted: {ProfileId}", profileId);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting access profile {ProfileId}", profileId);
            return Results.Json(new { error = "Failed to delete access profile" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> BulkCreateProfiles(
        BulkAccessProfilesRequest body,
        ClaimsPrincipal user,
        IAccessProfileRepository repository,
        IRepository<PlanData> planRepository,
        IRepository<ModelRoutingPolicy> routingPolicyRepository,
        ILogger<AccessProfile> logger)
    {
        if (body.Profiles is null || body.Profiles.Count == 0)
            return Results.BadRequest("At least one profile is required");

        var response = new BulkAccessProfilesResponse();

        for (var index = 0; index < body.Profiles.Count; index++)
        {
            try
            {
                var buildResult = await BuildProfileAsync(body.Profiles[index], repository, planRepository, routingPolicyRepository, user, logger);
                if (buildResult.ErrorResult is not null)
                {
                    response.Failed.Add(new BulkAccessProfileFailure
                    {
                        Index = index,
                        Error = buildResult.ErrorMessage ?? "Access profile request failed",
                        ProfileId = buildResult.ProfileId
                    });
                    continue;
                }

                await repository.UpsertAsync(buildResult.Profile!);
                response.Created++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating access profile at bulk index {Index}", index);
                response.Failed.Add(new BulkAccessProfileFailure
                {
                    Index = index,
                    Error = "Failed to create access profile",
                    ProfileId = TryBuildProfileId(body.Profiles[index])
                });
            }
        }

        return Results.Json(response, JsonConfig.Default);
    }

    private static async Task<BuildProfileResult> BuildProfileAsync(
        AccessProfileCreateRequest body,
        IAccessProfileRepository repository,
        IRepository<PlanData> planRepository,
        IRepository<ModelRoutingPolicy> routingPolicyRepository,
        ClaimsPrincipal user,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(body.ClientAppId) ||
            string.IsNullOrWhiteSpace(body.TenantId) ||
            string.IsNullOrWhiteSpace(body.ApiId) ||
            string.IsNullOrWhiteSpace(body.PlanId))
        {
            return new BuildProfileResult(Results.BadRequest("clientAppId, tenantId, apiId, and planId are required"), "clientAppId, tenantId, apiId, and planId are required", null, TryBuildProfileId(body));
        }

        var planId = body.PlanId.Trim();
        var plan = await planRepository.GetAsync(planId);
        if (plan is null)
            return new BuildProfileResult(Results.BadRequest($"Plan '{planId}' not found"), $"Plan '{planId}' not found", null, TryBuildProfileId(body));

        var routingPolicyId = NormalizeOptional(body.RoutingPolicyId);
        if (routingPolicyId is not null)
        {
            var routingPolicy = await routingPolicyRepository.GetAsync(routingPolicyId);
            if (routingPolicy is null)
                return new BuildProfileResult(Results.BadRequest($"Routing policy '{routingPolicyId}' not found"), $"Routing policy '{routingPolicyId}' not found", null, TryBuildProfileId(body));
        }

        var apiId = body.ApiId.Trim();
        var operationId = NormalizeOptional(body.OperationId);
        var profileId = AccessProfile.BuildId(body.ClientAppId, body.TenantId, apiId, operationId);

        var existing = await repository.GetAsync(profileId);
        if (existing is not null)
            return new BuildProfileResult(Results.Conflict(new { error = $"Access profile '{profileId}' already exists" }), $"Access profile '{profileId}' already exists", null, profileId);

        var now = DateTime.UtcNow;
        var profile = new AccessProfile
        {
            ClientAppId = body.ClientAppId.Trim(),
            TenantId = body.TenantId.Trim(),
            ApiId = apiId,
            OperationId = operationId,
            PlanId = planId,
            RoutingPolicyId = routingPolicyId,
            AllowedDeployments = NormalizeAllowedDeployments(body.AllowedDeployments),
            Enabled = body.Enabled,
            CreatedBy = GetActor(user),
            CreatedAt = now,
            UpdatedAt = now
        };

        return new BuildProfileResult(null, null, profile, profileId);
    }

    private static List<string> NormalizeAllowedDeployments(IEnumerable<string>? allowedDeployments)
        => (allowedDeployments ?? [])
            .Where(static deployment => !string.IsNullOrWhiteSpace(deployment))
            .Select(static deployment => deployment.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetActor(ClaimsPrincipal user)
        => user.FindFirstValue("preferred_username")
           ?? user.FindFirstValue(ClaimTypes.Upn)
           ?? user.Identity?.Name
           ?? "unknown";

    private static string? TryBuildProfileId(AccessProfileCreateRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.ClientAppId) ||
            string.IsNullOrWhiteSpace(body.TenantId) ||
            string.IsNullOrWhiteSpace(body.ApiId))
        {
            return null;
        }

        return AccessProfile.BuildId(body.ClientAppId, body.TenantId, body.ApiId, NormalizeOptional(body.OperationId));
    }

    private sealed record BuildProfileResult(IResult? ErrorResult, string? ErrorMessage, AccessProfile? Profile, string? ProfileId);
}
