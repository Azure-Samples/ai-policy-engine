namespace AIPolicyEngine.Api.Models;

public sealed class ModelPricingCreateRequest
{
    public string ModelId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public decimal PromptRatePer1K { get; set; }
    public decimal CompletionRatePer1K { get; set; }
    public decimal ImageRatePer1K { get; set; }

    /// <summary>Per-request billing multiplier. Null = use default (1.0).</summary>
    public decimal? Multiplier { get; set; }

    /// <summary>Pricing tier display name. Null = use default ("Standard").</summary>
    public string? TierName { get; set; }
}
