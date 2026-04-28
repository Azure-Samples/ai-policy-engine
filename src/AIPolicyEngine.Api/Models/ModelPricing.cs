namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Model cost rate configuration. Stored in Redis: pricing:{modelId}
/// Rates are per 1K tokens (USD).
/// </summary>
public sealed class ModelPricing
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = "pricing";
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal PromptRatePer1K { get; set; }
    public decimal CompletionRatePer1K { get; set; }
    public decimal ImageRatePer1K { get; set; }

    /// <summary>Per-request billing multiplier. 1.0 = baseline cost per request.</summary>
    public decimal Multiplier { get; set; } = 1.0m;

    /// <summary>Display grouping for pricing tiers (e.g. "Standard", "Premium").</summary>
    public string TierName { get; set; } = "Standard";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
