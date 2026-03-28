namespace Chargeback.Api.Models;

public sealed class ModelPricingCreateRequest
{
    public string ModelId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public decimal PromptRatePer1K { get; set; }
    public decimal CompletionRatePer1K { get; set; }
    public decimal ImageRatePer1K { get; set; }
}
