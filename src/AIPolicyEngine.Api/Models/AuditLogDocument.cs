using System.Text.Json.Serialization;

namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Individual request audit log stored in Cosmos DB for durable financial record-keeping.
/// Partition key: /customerKey (clientAppId:tenantId)
/// </summary>
public sealed class AuditLogDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Synthetic partition key: {clientAppId}:{tenantId}
    /// </summary>
    [JsonPropertyName("customerKey")]
    public string CustomerKey { get; set; } = string.Empty;

    [JsonPropertyName("clientAppId")]
    public string ClientAppId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    [JsonPropertyName("deploymentId")]
    public string DeploymentId { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("imageTokens")]
    public int ImageTokens { get; set; }

    [JsonPropertyName("costToUs")]
    public string CostToUs { get; set; } = "0.0000";

    [JsonPropertyName("costToCustomer")]
    public string CostToCustomer { get; set; } = "0.0000";

    [JsonPropertyName("isOverbilled")]
    public bool IsOverbilled { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>Original deployment the client requested (before routing).</summary>
    [JsonPropertyName("requestedDeploymentId")]
    public string? RequestedDeploymentId { get; set; }

    /// <summary>Routing policy that was applied (null = no routing).</summary>
    [JsonPropertyName("routingPolicyId")]
    public string? RoutingPolicyId { get; set; }

    /// <summary>Per-request billing multiplier applied (null = not using multiplier billing).</summary>
    [JsonPropertyName("multiplier")]
    public decimal? Multiplier { get; set; }

    /// <summary>Effective request cost after multiplier (null = not using multiplier billing).</summary>
    [JsonPropertyName("effectiveRequestCost")]
    public decimal? EffectiveRequestCost { get; set; }

    /// <summary>Pricing tier name for the deployment/model (null = not using multiplier billing).</summary>
    [JsonPropertyName("tierName")]
    public string? TierName { get; set; }

    /// <summary>
    /// Billing period in YYYY-MM format for efficient querying.
    /// </summary>
    [JsonPropertyName("billingPeriod")]
    public string BillingPeriod { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos DB TTL in seconds. Set from configuration (default: 36 months).
    /// </summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; set; } = 94608000; // 36 months
}
