using System.Text;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// Endpoints for request-based billing summaries and CSV export.
/// </summary>
public static class RequestBillingEndpoints
{
    public static IEndpointRouteBuilder MapRequestBillingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("")
            .RequireAuthorization("ExportPolicy");

        group.MapGet("/api/chargeback/request-summary", GetRequestSummary)
            .WithName("GetRequestSummary")
            .WithDescription("Get per-client effective request consumption, tier breakdown, and overage costs")
            .Produces<RequestSummaryResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/api/export/request-billing", ExportRequestBilling)
            .WithName("ExportRequestBilling")
            .WithDescription("Export request billing data as CSV")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status400BadRequest);

        return routes;
    }

    private static async Task<IResult> GetRequestSummary(
        int year, int month,
        IAuditStore auditStore,
        ILogger<RequestSummaryResponse> logger)
    {
        if (month < 1 || month > 12 || year < 2020 || year > 2099)
            return Results.BadRequest("Invalid year or month");

        try
        {
            var billingPeriod = $"{year:D4}-{month:D2}";
            var summaries = await auditStore.GetBillingSummariesAsync(billingPeriod);

            // Group by client (clientAppId:tenantId) across all deployments
            var clientGroups = summaries.GroupBy(
                s => $"{s.ClientAppId}:{s.TenantId}",
                StringComparer.OrdinalIgnoreCase);

            var clients = new List<RequestSummaryClient>();
            var globalTotals = new RequestSummaryTotals();

            foreach (var group in clientGroups)
            {
                var client = new RequestSummaryClient
                {
                    ClientAppId = group.First().ClientAppId,
                    TenantId = group.First().TenantId,
                    DisplayName = group.First().DisplayName
                };

                foreach (var summary in group)
                {
                    client.TotalEffectiveRequests += summary.TotalEffectiveRequests ?? 0;
                    client.MultiplierOverageCost += summary.MultiplierOverageCost ?? 0;
                    client.RawRequestCount += summary.RequestCount;

                    if (summary.EffectiveRequestsByTier is not null)
                    {
                        foreach (var (tier, count) in summary.EffectiveRequestsByTier)
                        {
                            if (!client.EffectiveRequestsByTier.ContainsKey(tier))
                                client.EffectiveRequestsByTier[tier] = 0;
                            client.EffectiveRequestsByTier[tier] += count;
                        }
                    }
                }

                clients.Add(client);

                // Accumulate global totals
                globalTotals.TotalEffectiveRequests += client.TotalEffectiveRequests;
                globalTotals.TotalMultiplierOverageCost += client.MultiplierOverageCost;
                globalTotals.TotalRawRequests += client.RawRequestCount;

                foreach (var (tier, count) in client.EffectiveRequestsByTier)
                {
                    if (!globalTotals.EffectiveRequestsByTier.ContainsKey(tier))
                        globalTotals.EffectiveRequestsByTier[tier] = 0;
                    globalTotals.EffectiveRequestsByTier[tier] += count;
                }
            }

            return Results.Ok(new RequestSummaryResponse
            {
                BillingPeriod = billingPeriod,
                Clients = clients.OrderBy(c => c.DisplayName).ToList(),
                Totals = globalTotals
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating request summary for {Year}-{Month}", year, month);
            return Results.Json(new { error = "Failed to generate request summary" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ExportRequestBilling(
        int year, int month,
        IAuditStore auditStore,
        ILogger<RequestSummaryResponse> logger)
    {
        if (month < 1 || month > 12 || year < 2020 || year > 2099)
            return Results.BadRequest("Invalid year or month");

        try
        {
            var billingPeriod = $"{year:D4}-{month:D2}";
            var summaries = await auditStore.GetBillingSummariesAsync(billingPeriod);

            var sb = new StringBuilder();
            sb.AppendLine("ClientAppId,TenantId,DisplayName,DeploymentId,Model,RawRequests,TotalEffectiveRequests,MultiplierOverageCost,TotalTokens,CostToUs,CostToCustomer");

            foreach (var s in summaries)
            {
                sb.AppendLine($"{Escape(s.ClientAppId)},{Escape(s.TenantId)},{Escape(s.DisplayName)},{Escape(s.DeploymentId)},{Escape(s.Model)},{s.RequestCount},{s.TotalEffectiveRequests?.ToString("F4") ?? "0.0000"},{s.MultiplierOverageCost?.ToString("F4") ?? "0.0000"},{s.TotalTokens},{s.CostToUs:F4},{s.CostToCustomer:F4}");
            }

            var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var filename = $"request-billing-{billingPeriod}.csv";

            var isCurrentMonth = year == DateTime.UtcNow.Year && month == DateTime.UtcNow.Month;
            var fileResult = Results.File(csvBytes, contentType: "text/csv", fileDownloadName: filename);

            return isCurrentMonth ? new IncompleteDataResult(fileResult) : fileResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error exporting request billing for {Year}-{Month}", year, month);
            return Results.Json(new { error = "Failed to export request billing" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private sealed class IncompleteDataResult(IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["X-Data-Incomplete"] = "true";
            await inner.ExecuteAsync(httpContext);
        }
    }
}
