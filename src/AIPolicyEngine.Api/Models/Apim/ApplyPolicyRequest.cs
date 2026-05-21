using System.Text.Json;

namespace AIPolicyEngine.Api.Models.Apim;

public sealed class ApplyPolicyRequest
{
    public string TemplateId { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
}
