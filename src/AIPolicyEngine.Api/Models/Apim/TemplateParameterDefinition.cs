using System.Text.Json;

namespace AIPolicyEngine.Api.Models.Apim;

public sealed class TemplateParameterDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string Description { get; set; } = string.Empty;
    public JsonElement? Default { get; set; }
}
