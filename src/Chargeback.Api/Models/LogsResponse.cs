namespace Chargeback.Api.Models;

/// <summary>
/// Legacy response kept for backward compatibility.
/// </summary>
public sealed class LogsResponse
{
    public List<LogEntry> AggregatedLogs { get; set; } = [];
}
