using System.Text.Json;

namespace AIPolicyEngine.Api.Models.Apim;

public sealed class RenderedTemplate
{
    public TemplateManifest Manifest { get; set; } = new();
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
    public string Xml { get; set; } = string.Empty;
}
