using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Shared service for reading aggregated log data and request traces.
/// Aggregate summaries come from Cosmos DB billing summaries for the full billing period.
/// Real-time request traces come from Redis.
/// </summary>
public interface ILogDataService
{
    /// <summary>
    /// Returns aggregated usage summaries for the current billing period from Cosmos DB.
    /// Each entry represents a client+deployment combination with totals for the full period.
    /// </summary>
    Task<List<LogEntry>> GetBillingPeriodSummariesAsync(ILogger logger, CancellationToken ct = default);

    /// <summary>
    /// Scans all Redis log:* keys for real-time cached data with calculated costs.
    /// Represents the short-term Redis window, useful for live dashboard views.
    /// </summary>
    Task<List<LogEntry>> GetAllLogsAsync(ILogger logger);

    /// <summary>
    /// Reads trace records from all traces:* Redis lists and builds request log entries
    /// with client display name resolution.
    /// </summary>
    Task<List<RequestLogEntry>> GetRequestLogsAsync(ILogger logger);
}
