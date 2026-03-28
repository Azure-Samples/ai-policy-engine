namespace Chargeback.Api.Models;

/// <summary>
/// Item enqueued into the audit log channel for background persistence to Cosmos DB.
/// </summary>
public sealed class AuditLogItem
{
    public string ClientAppId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string DeploymentId { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int ImageTokens { get; set; }
    public string CostToUs { get; set; } = "0.0000";
    public string CostToCustomer { get; set; } = "0.0000";
    public bool IsOverbilled { get; set; }
    public int StatusCode { get; set; } = 200;
    public DateTime Timestamp { get; set; }
}
