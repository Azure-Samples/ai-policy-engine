using System.Text.Json.Serialization;

namespace Chargeback.Api.Models;

/// <summary>
/// Monthly billing summary per customer+deployment stored in Cosmos DB.
/// A "Customer" is the clientAppId:tenantId combination.
/// Incrementally updated via upsert during batch writes.
/// Partition key: /customerKey
/// </summary>
public sealed class BillingSummaryDocument
{
    /// <summary>
    /// Composite key: {clientAppId}:{tenantId}:{deploymentId}:{billingPeriod}
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Synthetic partition key: {clientAppId}:{tenantId}
    /// </summary>
    [JsonPropertyName("customerKey")]
    public string CustomerKey { get; set; } = string.Empty;

    [JsonPropertyName("clientAppId")]
    public string ClientAppId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    [JsonPropertyName("deploymentId")]
    public string DeploymentId { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Billing period in YYYY-MM format.
    /// </summary>
    [JsonPropertyName("billingPeriod")]
    public string BillingPeriod { get; set; } = string.Empty;

    [JsonPropertyName("promptTokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("imageTokens")]
    public long ImageTokens { get; set; }

    [JsonPropertyName("costToUs")]
    public decimal CostToUs { get; set; }

    [JsonPropertyName("costToCustomer")]
    public decimal CostToCustomer { get; set; }

    [JsonPropertyName("isOverbilled")]
    public bool IsOverbilled { get; set; }

    [JsonPropertyName("requestCount")]
    public long RequestCount { get; set; }

    /// <summary>Total effective requests (multiplier-weighted) for this period.</summary>
    [JsonPropertyName("totalEffectiveRequests")]
    public decimal? TotalEffectiveRequests { get; set; }

    /// <summary>Effective requests broken down by pricing tier.</summary>
    [JsonPropertyName("effectiveRequestsByTier")]
    public Dictionary<string, decimal>? EffectiveRequestsByTier { get; set; }

    /// <summary>Total overage cost from multiplier billing for this period.</summary>
    [JsonPropertyName("multiplierOverageCost")]
    public decimal? MultiplierOverageCost { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Cosmos DB TTL in seconds. Set from configuration (default: 36 months).
    /// </summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; set; } = 94608000; // 36 months
}
