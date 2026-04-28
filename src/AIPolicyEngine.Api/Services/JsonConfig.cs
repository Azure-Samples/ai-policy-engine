using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Shared JsonSerializerOptions to avoid duplicating configuration across endpoint classes.
/// </summary>
public static class JsonConfig
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}
