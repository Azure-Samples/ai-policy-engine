namespace AIPolicyEngine.Api.Models.Apim;

public sealed class TemplateManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<TemplateParameterDefinition> Parameters { get; set; } = [];
    public string Scope { get; set; } = "api";
}
