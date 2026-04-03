using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// Runtime usage policy settings endpoints.
/// </summary>
public static class UsagePolicyEndpoints
{
    public static IEndpointRouteBuilder MapUsagePolicyEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/settings/usage-policy", GetUsagePolicy)
            .WithName("GetUsagePolicy")
            .WithDescription("Get usage policy settings for billing cycle and retention")
            .RequireAuthorization()
            .Produces<UsagePolicySettings>();

        routes.MapPut("/api/settings/usage-policy", UpdateUsagePolicy)
            .WithName("UpdateUsagePolicy")
            .WithDescription("Update usage policy settings for billing cycle and retention")
            .RequireAuthorization("AdminPolicy")
            .Produces<UsagePolicySettings>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> GetUsagePolicy(
        IUsagePolicyStore usagePolicyStore,
        ILogger<UsagePolicySettings> logger)
    {
        try
        {
            var settings = await usagePolicyStore.GetAsync();
            return Results.Json(settings, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching usage policy settings");
            return Results.Json(new { error = "Failed to fetch usage policy settings" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpdateUsagePolicy(
        UsagePolicyUpdateRequest request,
        IUsagePolicyStore usagePolicyStore,
        ILogger<UsagePolicySettings> logger)
    {
        try
        {
            if (request.BillingCycleStartDay is < 1 or > 31)
                return Results.BadRequest("billingCycleStartDay must be between 1 and 31");
            if (request.AggregatedLogRetentionDays is < 1 or > 365)
                return Results.BadRequest("aggregatedLogRetentionDays must be between 1 and 365");
            if (request.TraceRetentionDays is < 1 or > 365)
                return Results.BadRequest("traceRetentionDays must be between 1 and 365");

            var settings = await usagePolicyStore.UpdateAsync(request);
            return Results.Json(settings, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating usage policy settings");
            return Results.Json(new { error = "Failed to update usage policy settings" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
