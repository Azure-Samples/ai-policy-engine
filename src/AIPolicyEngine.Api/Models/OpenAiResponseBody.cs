namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Represents the relevant fields from an Azure OpenAI response.
/// </summary>
public sealed class OpenAiResponseBody
{
    public string? Model { get; set; }
    public string? Object { get; set; }
    public UsageData? Usage { get; set; }
}
