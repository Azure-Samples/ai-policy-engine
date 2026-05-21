namespace AIPolicyEngine.Api.Models.Apim;

public sealed class ApimApiSummary
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
}
