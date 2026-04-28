namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Response from GET /api/logs containing individual request log entries.
/// </summary>
public sealed class RequestLogsResponse
{
    public List<RequestLogEntry> Entries { get; set; } = [];
    public int TotalCount { get; set; }
}
