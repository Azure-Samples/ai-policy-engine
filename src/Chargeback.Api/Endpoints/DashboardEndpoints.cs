using Chargeback.Api.Models;
using Chargeback.Api.Services;
using StackExchange.Redis;

namespace Chargeback.Api.Endpoints;

/// <summary>
/// Dashboard API endpoints for viewing usage summaries, request logs, and chargeback data.
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/usage", GetUsageSummary)
            .WithName("GetUsageSummary")
            .WithDescription("Fetch aggregated usage summaries with calculated costs")
            .RequireAuthorization()
            .Produces<UsageSummaryResponse>();

        routes.MapGet("/api/logs", GetRequestLogs)
            .WithName("GetRequestLogs")
            .WithDescription("Fetch individual request log entries from trace records")
            .RequireAuthorization()
            .Produces<RequestLogsResponse>();

        routes.MapGet("/chargeback", GetChargeback)
            .WithName("GetChargeback")
            .WithDescription("Calculate total chargeback and return itemized logs")
            .RequireAuthorization()
            .Produces<ChargebackResponse>();

        routes.MapDelete("/api/admin/redis/{*key}", async (string key, IConnectionMultiplexer redis, ILogger<LogsResponse> logger) =>
        {
            var db = redis.GetDatabase();
            var deleted = await db.KeyDeleteAsync(key);
            logger.LogInformation("Admin delete key={Key}, deleted={Deleted}", key, deleted);
            return deleted ? Results.Ok(new { deleted = key }) : Results.NotFound(new { error = $"Key '{key}' not found" });
        }).WithName("AdminDeleteKey").WithDescription("Delete a Redis key (admin)").RequireAuthorization("AdminPolicy");

        return routes;
    }

    private static async Task<IResult> GetUsageSummary(
        ILogDataService logDataService,
        ILogger<UsageSummaryResponse> logger)
    {
        try
        {
            var logs = await logDataService.GetBillingPeriodSummariesAsync(logger);
            return Results.Json(new UsageSummaryResponse { UsageSummaries = logs }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching usage summaries");
            return Results.Json(new { error = "Failed to fetch usage summaries" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetRequestLogs(
        ILogDataService logDataService,
        ILogger<RequestLogsResponse> logger)
    {
        try
        {
            var entries = await logDataService.GetRequestLogsAsync(logger);
            var totalCount = entries.Count;
            if (entries.Count > 200)
                entries = entries.GetRange(0, 200);

            return Results.Json(new RequestLogsResponse { Entries = entries, TotalCount = totalCount }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching request logs");
            return Results.Json(new { error = "Failed to fetch request logs" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetChargeback(
        ILogDataService logDataService,
        ILogger<ChargebackResponse> logger)
    {
        try
        {
            var logs = await logDataService.GetBillingPeriodSummariesAsync(logger);
            var totalChargeback = logs.Sum(l => decimal.Parse(l.TotalCost, System.Globalization.CultureInfo.InvariantCulture));

            return Results.Json(new ChargebackResponse
            {
                TotalChargeback = totalChargeback.ToString("F2"),
                Logs = logs
            }, JsonConfig.Default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calculating chargeback");
            return Results.Json(new { error = "Failed to calculate chargeback" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
