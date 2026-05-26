using System.Text.Json;
using AIPolicyEngine.Api.Models.Apim;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public interface ITemplateLibraryService
{
    Task<IReadOnlyList<TemplateManifest>> ListTemplatesAsync(CancellationToken ct = default);
    Task<RenderedTemplate> RenderAsync(string templateId, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken ct = default);
}
