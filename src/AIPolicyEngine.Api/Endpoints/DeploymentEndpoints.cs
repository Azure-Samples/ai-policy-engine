using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Api.Services.ApimManagement;

namespace AIPolicyEngine.Api.Endpoints;

public static class DeploymentEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/deployments", GetDeployments)
            .WithName("GetDeployments")
            .WithDescription("List available Azure OpenAI deployments from the Foundry resource")
            .RequireAuthorization()
            .Produces<DeploymentsResponse>();

        routes.MapPost("/api/deployments/onboard", OnboardDeployments)
            .WithName("OnboardFoundryDeployments")
            .WithDescription("Grant APIM access to every discovered Foundry resource and refresh assigned APIM policies")
            .RequireAuthorization("AdminPolicy")
            .Produces<FoundryOnboardingResponse>();

        return routes;
    }

    private static async Task<IResult> GetDeployments(
        IDeploymentDiscoveryService deploymentService,
        ILogger<DeploymentsResponse> logger)
    {
        try
        {
            var deployments = await deploymentService.GetDeploymentsAsync();
            return Results.Json(new DeploymentsResponse { Deployments = deployments }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching deployments");
            return Results.Json(new { error = "Failed to fetch deployments" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> OnboardDeployments(
        IDeploymentDiscoveryService deploymentService,
        IApimPolicyApplyService policyApplyService,
        ILogger<DeploymentsResponse> logger,
        CancellationToken ct)
    {
        try
        {
            var deployments = await deploymentService.GetDeploymentsAsync(ct);
            var resourcesOnboarded = await deploymentService.OnboardAsync(ct);
            var policiesQueued = await policyApplyService.QueueAllPolicyReapplyAsync(ct);
            return Results.Json(new FoundryOnboardingResponse
            {
                ResourcesOnboarded = resourcesOnboarded,
                DeploymentsDiscovered = deployments.Count,
                PoliciesQueuedForReapply = policiesQueued
            }, JsonConfig.Default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return OnboardingFailure(ex, logger);
        }
        catch (InvalidOperationException ex)
        {
            return OnboardingFailure(ex, logger);
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            return OnboardingFailure(ex, logger);
        }
    }

    private static IResult OnboardingFailure(Exception ex, ILogger logger)
    {
        logger.LogError(ex, "Failed to onboard Foundry resources");
        return Results.Json(
            new { error = "Failed to onboard Foundry resources" },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}
