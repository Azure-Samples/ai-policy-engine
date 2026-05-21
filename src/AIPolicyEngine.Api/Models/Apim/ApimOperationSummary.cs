namespace AIPolicyEngine.Api.Models.Apim;

public sealed class ApimOperationSummary
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string UrlTemplate { get; set; } = string.Empty;
}
