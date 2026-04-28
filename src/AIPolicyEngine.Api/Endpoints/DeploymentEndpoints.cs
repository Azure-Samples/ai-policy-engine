using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

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
}
