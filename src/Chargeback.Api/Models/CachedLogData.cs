namespace Chargeback.Api.Models;

/// <summary>
/// Cached log data stored in Redis. Represents aggregated token usage
/// for a specific tenant + client app + deployment combination.
/// </summary>
public sealed class CachedLogData
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
    public decimal CostToUs { get; set; }
    public decimal CostToCustomer { get; set; }
    public bool IsOverbilled { get; set; }
}
