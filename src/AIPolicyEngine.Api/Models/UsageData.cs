namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Token usage data from the Azure OpenAI response.
/// Uses snake_case names to match OpenAI API response format (what APIM forwards).
/// </summary>
public sealed class UsageData
{
    [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("promptTokens")]
    public int PromptTokensCamelCase { set => PromptTokens = value; }

    [System.Text.Json.Serialization.JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("completionTokens")]
    public int CompletionTokensCamelCase { set => CompletionTokens = value; }

    [System.Text.Json.Serialization.JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTokens")]
    public int TotalTokensCamelCase { set => TotalTokens = value; }

    [System.Text.Json.Serialization.JsonPropertyName("image_tokens")]
    public int ImageTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("imageTokens")]
    public int ImageTokensCamelCase { set => ImageTokens = value; }
}
