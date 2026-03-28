namespace Chargeback.Api.Models;

/// <summary>
/// Token usage data from the Azure OpenAI response.
/// Uses snake_case names to match OpenAI API response format (what APIM forwards).
/// </summary>
public sealed class UsageData
{
    [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("image_tokens")]
    public int ImageTokens { get; set; }
}
