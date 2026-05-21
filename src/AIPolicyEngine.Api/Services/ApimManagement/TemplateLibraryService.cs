using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPolicyEngine.Api.Models.Apim;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public sealed class TemplateLibraryService : ITemplateLibraryService
{
    private static readonly Regex PlaceholderRegex = new("\\{\\{(?<name>[A-Za-z0-9_]+)\\}\\}", RegexOptions.Compiled);

    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<TemplateLibraryService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyDictionary<string, TemplateDefinition>? _templates;

    public TemplateLibraryService(IHostEnvironment hostEnvironment, ILogger<TemplateLibraryService> logger)
    {
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TemplateManifest>> ListTemplatesAsync(CancellationToken ct = default)
    {
        var templates = await GetTemplatesAsync(ct);
        return templates.Values
            .Select(definition => CloneManifest(definition.Manifest))
            .OrderBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RenderedTemplate> RenderAsync(
        string templateId,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new TemplateValidationException("templateId is required.");
        }

        var templates = await GetTemplatesAsync(ct);
        if (!templates.TryGetValue(templateId, out var template))
        {
            throw new TemplateValidationException($"Template '{templateId}' was not found.");
        }

        var manifestNames = template.Manifest.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        var unknownParameters = parameters.Keys.Where(key => !manifestNames.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (unknownParameters.Count > 0)
        {
            throw new TemplateValidationException(
                $"Template '{templateId}' does not define parameter(s): {string.Join(", ", unknownParameters)}.");
        }

        var normalizedParameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var parameter in template.Manifest.Parameters)
        {
            JsonElement candidateValue;
            if (parameters.TryGetValue(parameter.Name, out var providedValue))
            {
                candidateValue = providedValue.Clone();
            }
            else if (parameter.Default.HasValue)
            {
                candidateValue = parameter.Default.Value.Clone();
            }
            else if (parameter.Required)
            {
                throw new TemplateValidationException(
                    $"Template '{templateId}' requires parameter '{parameter.Name}'.");
            }
            else
            {
                throw new TemplateValidationException(
                    $"Optional parameter '{parameter.Name}' in template '{templateId}' must declare a default value.");
            }

            normalizedParameters[parameter.Name] = NormalizeParameterValue(templateId, parameter, candidateValue);
        }

        var renderedXml = template.PolicyXml;
        foreach (var parameter in template.Manifest.Parameters)
        {
            renderedXml = renderedXml.Replace(
                $"{{{{{parameter.Name}}}}}",
                ConvertToReplacementValue(parameter, normalizedParameters[parameter.Name]),
                StringComparison.Ordinal);
        }

        var unresolvedPlaceholders = PlaceholderRegex.Matches(renderedXml)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (unresolvedPlaceholders.Count > 0)
        {
            throw new TemplateValidationException(
                $"Template '{templateId}' still contains unresolved placeholder(s): {string.Join(", ", unresolvedPlaceholders)}.");
        }

        ValidateRenderedXml(templateId, renderedXml);

        return new RenderedTemplate
        {
            Manifest = CloneManifest(template.Manifest),
            Parameters = normalizedParameters,
            Xml = renderedXml
        };
    }

    private async Task<IReadOnlyDictionary<string, TemplateDefinition>> GetTemplatesAsync(CancellationToken ct)
    {
        if (_templates is not null)
        {
            return _templates;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_templates is not null)
            {
                return _templates;
            }

            var templateRoot = ResolveTemplateRoot();
            var templates = new Dictionary<string, TemplateDefinition>(StringComparer.Ordinal);

            foreach (var directory in Directory.GetDirectories(templateRoot))
            {
                var manifestPath = Path.Combine(directory, "template.json");
                var policyPath = Path.Combine(directory, "policy.xml");

                if (!File.Exists(manifestPath) || !File.Exists(policyPath))
                {
                    throw new InvalidOperationException($"Template directory '{directory}' must contain both template.json and policy.xml.");
                }

                var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = JsonSerializer.Deserialize<TemplateManifest>(manifestJson, JsonConfig.Default)
                    ?? throw new InvalidOperationException($"Template manifest '{manifestPath}' could not be deserialized.");
                manifest.Parameters ??= [];
                foreach (var parameter in manifest.Parameters)
                {
                    parameter.Description ??= string.Empty;
                    parameter.Type = parameter.Type.Trim();
                    if (parameter.Default.HasValue)
                    {
                        parameter.Default = parameter.Default.Value.Clone();
                    }
                }

                var policyXml = await File.ReadAllTextAsync(policyPath, ct);
                ValidateManifestAgainstPolicy(manifest, policyXml, policyPath);

                if (templates.ContainsKey(manifest.Id))
                {
                    throw new InvalidOperationException($"Duplicate APIM template id '{manifest.Id}' found under '{templateRoot}'.");
                }

                templates[manifest.Id] = new TemplateDefinition(CloneManifest(manifest), policyXml);
            }

            _logger.LogInformation("Loaded {Count} APIM policy templates from {TemplateRoot}", templates.Count, templateRoot);
            _templates = templates;
            return _templates;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private string ResolveTemplateRoot()
    {
        var contentRootCandidate = Path.Combine(_hostEnvironment.ContentRootPath, "policies", "templates");
        if (Directory.Exists(contentRootCandidate))
        {
            return contentRootCandidate;
        }

        var repoRootCandidate = Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, "..", "..", "policies", "templates"));
        if (Directory.Exists(repoRootCandidate))
        {
            return repoRootCandidate;
        }

        throw new DirectoryNotFoundException(
            $"Unable to locate policies/templates under '{_hostEnvironment.ContentRootPath}'.");
    }

    private static void ValidateManifestAgainstPolicy(TemplateManifest manifest, string policyXml, string policyPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidOperationException($"Template manifest '{policyPath}' must declare a non-empty id.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            throw new InvalidOperationException($"Template manifest '{manifest.Id}' must declare a non-empty displayName.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException($"Template manifest '{manifest.Id}' must declare a non-empty version.");
        }

        var manifestParameterNames = manifest.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (manifestParameterNames.Count != manifest.Parameters.Count)
        {
            throw new InvalidOperationException($"Template manifest '{manifest.Id}' contains duplicate parameter names.");
        }

        var placeholders = PlaceholderRegex.Matches(policyXml)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var missingParameters = placeholders.Where(name => !manifestParameterNames.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (missingParameters.Count > 0)
        {
            throw new InvalidOperationException(
                $"Template manifest '{manifest.Id}' is missing parameter(s) referenced by policy.xml: {string.Join(", ", missingParameters)}.");
        }

        var extraParameters = manifestParameterNames.Where(name => !placeholders.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (extraParameters.Count > 0)
        {
            throw new InvalidOperationException(
                $"Template manifest '{manifest.Id}' declares parameter(s) not present in policy.xml: {string.Join(", ", extraParameters)}.");
        }
    }

    private static JsonElement NormalizeParameterValue(string templateId, TemplateParameterDefinition definition, JsonElement value)
    {
        var type = definition.Type.ToLowerInvariant();
        switch (type)
        {
            case "string":
                if (value.ValueKind == JsonValueKind.String)
                {
                    return JsonSerializer.SerializeToElement(value.GetString() ?? string.Empty, JsonConfig.Default);
                }
                break;
            case "int":
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
                {
                    return JsonSerializer.SerializeToElement(intValue, JsonConfig.Default);
                }
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                {
                    return JsonSerializer.SerializeToElement(intValue, JsonConfig.Default);
                }
                break;
            case "decimal":
            case "number":
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
                {
                    return JsonSerializer.SerializeToElement(decimalValue, JsonConfig.Default);
                }
                if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimalValue))
                {
                    return JsonSerializer.SerializeToElement(decimalValue, JsonConfig.Default);
                }
                break;
            case "bool":
            case "boolean":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonSerializer.SerializeToElement(value.GetBoolean(), JsonConfig.Default);
                }
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var boolValue))
                {
                    return JsonSerializer.SerializeToElement(boolValue, JsonConfig.Default);
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Template '{templateId}' declares unsupported parameter type '{definition.Type}' for '{definition.Name}'.");
        }

        throw new TemplateValidationException(
            $"Template '{templateId}' parameter '{definition.Name}' must be of type '{definition.Type}'.");
    }

    private static string ConvertToReplacementValue(TemplateParameterDefinition definition, JsonElement value)
    {
        return definition.Type.ToLowerInvariant() switch
        {
            "string" => value.GetString() ?? string.Empty,
            "int" => value.GetInt32().ToString(CultureInfo.InvariantCulture),
            "decimal" or "number" => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
            "bool" or "boolean" => value.GetBoolean() ? "true" : "false",
            _ => throw new InvalidOperationException($"Unsupported parameter type '{definition.Type}'.")
        };
    }

    private static void ValidateRenderedXml(string templateId, string renderedXml)
    {
        var startIndex = renderedXml.IndexOf("<policies", StringComparison.Ordinal);
        var endIndex = renderedXml.LastIndexOf("</policies>", StringComparison.Ordinal);

        if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
        {
            throw new TemplateValidationException($"Template '{templateId}' must render an XML document rooted at <policies>.");
        }
    }

    private static TemplateManifest CloneManifest(TemplateManifest manifest)
        => new()
        {
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            Version = manifest.Version,
            Scope = manifest.Scope,
            Parameters = manifest.Parameters.Select(CloneParameter).ToList()
        };

    private static TemplateParameterDefinition CloneParameter(TemplateParameterDefinition parameter)
        => new()
        {
            Name = parameter.Name,
            Type = parameter.Type,
            Required = parameter.Required,
            Description = parameter.Description,
            Default = parameter.Default.HasValue ? parameter.Default.Value.Clone() : null
        };

    private sealed record TemplateDefinition(TemplateManifest Manifest, string PolicyXml);
}
