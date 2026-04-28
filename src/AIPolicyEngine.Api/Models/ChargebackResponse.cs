namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Response from the GET /chargeback endpoint.
/// Keeps "Logs" JSON name for backward compatibility.
/// </summary>
public sealed class ChargebackResponse
{
    public string TotalChargeback { get; set; } = "0.00";
    public List<LogEntry> Logs { get; set; } = [];
}
