namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Response from GET /api/usage (and legacy /logs).
/// </summary>
public sealed class UsageSummaryResponse
{
    public List<LogEntry> UsageSummaries { get; set; } = [];
}
