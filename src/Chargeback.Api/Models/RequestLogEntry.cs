namespace Chargeback.Api.Models;

/// <summary>
/// Individual request log entry sourced from trace records, enriched with client info.
/// </summary>
public sealed class RequestLogEntry
{
    public DateTime Timestamp { get; set; }
    public string ClientAppId { get; set; } = string.Empty;
    public string ClientDisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DeploymentId { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public string CostToUs { get; set; } = "0.00";
    public string CostToCustomer { get; set; } = "0.00";
    public bool IsOverbilled { get; set; }
    public int StatusCode { get; set; } = 200;
}
