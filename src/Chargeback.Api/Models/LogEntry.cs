namespace Chargeback.Api.Models;

/// <summary>
/// Aggregated usage summary returned by the dashboard API, including calculated cost.
/// Also aliased as LogEntry for backward compatibility.
/// </summary>
public sealed class LogEntry
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientAppId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string DeploymentId { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? ObjectType { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long ImageTokens { get; set; }
    public string TotalCost { get; set; } = "0.00";
    public string CostToUs { get; set; } = "0.00";
    public string CostToCustomer { get; set; } = "0.00";
    public bool IsOverbilled { get; set; }
}
