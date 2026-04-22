using System.Text;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// Export endpoints for downloading AI policy data as CSV.
/// Uses Cosmos DB as primary source, falls back to Redis for pre-existing data.
/// CSV downloads require the AIPolicy.Export app role.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder routes)
    {
        // Available periods is metadata — accessible to any authenticated user
        routes.MapGet("/api/export/available-periods", GetAvailablePeriods)
            .WithName("GetAvailablePeriods")
            .WithDescription("Get available billing periods and clients for export")
            .RequireAuthorization()
            .Produces<ExportPeriodsResponse>();

        // CSV downloads require the AIPolicy.Export role
        var exportGroup = routes.MapGroup("/api/export")
            .RequireAuthorization("ExportPolicy");

        exportGroup.MapGet("/billing-summary", ExportBillingSummary)
            .WithName("ExportBillingSummary")
            .WithDescription("Export monthly billing summary for all clients as CSV")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        exportGroup.MapGet("/client-audit", ExportClientAudit)
            .WithName("ExportClientAudit")
            .WithDescription("Export detailed audit trail for a specific client as CSV")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> GetAvailablePeriods(
        IAuditStore auditStore,
        ILogger<ExportPeriodsResponse> logger)
    {
        try
        {
            var periods = await auditStore.GetAvailablePeriodsAsync();
            var now = DateTime.UtcNow;
            var currentPeriod = new ExportPeriod { Year = now.Year, Month = now.Month };

            var clients = new List<ExportClient>();
            if (periods.Count > 0)
            {
                var latestPeriod = periods.First();
                clients = await auditStore.GetClientsForPeriodAsync(
                    $"{latestPeriod.Year:D4}-{latestPeriod.Month:D2}");
            }

            return Results.Ok(new ExportPeriodsResponse
            {
                Periods = periods,
                CurrentPeriod = currentPeriod,
                Clients = clients
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching available periods");
            return Results.Json(new { error = "Failed to fetch available periods" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ExportBillingSummary(
        int year, int month,
        IAuditStore auditStore,
        ILogger<ExportPeriodsResponse> logger)
    {
        if (month < 1 || month > 12 || year < 2020 || year > 2099)
            return Results.BadRequest("Invalid year or month");

        try
        {
            var billingPeriod = $"{year:D4}-{month:D2}";
            var summaries = await auditStore.GetBillingSummariesAsync(billingPeriod);

            var sb = new StringBuilder();
            sb.AppendLine("ClientAppId,TenantId,DisplayName,DeploymentId,Model,PromptTokens,CompletionTokens,TotalTokens,ImageTokens,CostToUs,CostToCustomer,IsOverbilled,RequestCount");

            foreach (var s in summaries)
            {
                sb.AppendLine($"{Escape(s.ClientAppId)},{Escape(s.TenantId)},{Escape(s.DisplayName)},{Escape(s.DeploymentId)},{Escape(s.Model)},{s.PromptTokens},{s.CompletionTokens},{s.TotalTokens},{s.ImageTokens},{s.CostToUs:F4},{s.CostToCustomer:F4},{s.IsOverbilled},{s.RequestCount}");
            }

            var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var filename = $"billing-summary-{billingPeriod}.csv";

            var isCurrentMonth = year == DateTime.UtcNow.Year && month == DateTime.UtcNow.Month;
            var fileResult = Results.File(csvBytes, contentType: "text/csv", fileDownloadName: filename);

            return isCurrentMonth ? new IncompleteDataResult(fileResult) : fileResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error exporting billing summary for {Year}-{Month}", year, month);
            return Results.Json(new { error = "Failed to export billing summary" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ExportClientAudit(
        string clientAppId, string tenantId, int year, int month,
        IAuditStore auditStore,
        ILogger<ExportPeriodsResponse> logger)
    {
        if (string.IsNullOrWhiteSpace(clientAppId))
            return Results.BadRequest("clientAppId is required");
        if (string.IsNullOrWhiteSpace(tenantId))
            return Results.BadRequest("tenantId is required");
        if (month < 1 || month > 12 || year < 2020 || year > 2099)
            return Results.BadRequest("Invalid year or month");

        try
        {
            var billingPeriod = $"{year:D4}-{month:D2}";
            var customerKey = $"{clientAppId}:{tenantId}";
            var logs = await auditStore.GetClientAuditLogsAsync(customerKey, billingPeriod);

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,ClientAppId,TenantId,DeploymentId,Model,PromptTokens,CompletionTokens,TotalTokens,ImageTokens,CostToUs,CostToCustomer,IsOverbilled,StatusCode");

            foreach (var log in logs)
            {
                sb.AppendLine($"{log.Timestamp:O},{Escape(log.ClientAppId)},{Escape(log.TenantId)},{Escape(log.DeploymentId)},{Escape(log.Model)},{log.PromptTokens},{log.CompletionTokens},{log.TotalTokens},{log.ImageTokens},{log.CostToUs},{log.CostToCustomer},{log.IsOverbilled},{log.StatusCode}");
            }

            var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var safeClientId = clientAppId.Replace(":", "-").Replace("/", "-");
            var safeTenantId = tenantId.Replace(":", "-").Replace("/", "-");
            var filename = $"client-audit-{safeClientId}-{safeTenantId}-{billingPeriod}.csv";

            var isCurrentMonth = year == DateTime.UtcNow.Year && month == DateTime.UtcNow.Month;
            var fileResult = Results.File(csvBytes, contentType: "text/csv", fileDownloadName: filename);

            return isCurrentMonth ? new IncompleteDataResult(fileResult) : fileResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error exporting client audit for {ClientAppId}/{TenantId} {Year}-{Month}",
                clientAppId, tenantId, year, month);
            return Results.Json(new { error = "Failed to export client audit" },
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

    /// <summary>
    /// Wraps an IResult to add X-Data-Incomplete header when exporting current month data.
    /// </summary>
    private sealed class IncompleteDataResult(IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["X-Data-Incomplete"] = "true";
            await inner.ExecuteAsync(httpContext);
        }
    }
}
