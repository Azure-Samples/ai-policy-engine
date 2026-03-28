namespace Chargeback.Api.Models;

/// <summary>
/// Represents the log data received from the APIM outbound policy.
/// Contains Entra JWT claims (tid, aud, azp) for tenant-based chargeback tracking.
/// </summary>
public sealed class LogIngestRequest
{
    /// <summary>Entra tenant ID (tid claim from JWT).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Client application ID (azp claim from JWT).</summary>
    public string ClientAppId { get; set; } = string.Empty;

    /// <summary>Token audience (aud claim from JWT).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Azure OpenAI deployment ID from the request URL path.</summary>
    public string DeploymentId { get; set; } = string.Empty;

    /// <summary>Raw request body forwarded from APIM (for Purview evaluation).</summary>
    public object? RequestBody { get; set; }

    /// <summary>Parsed response body from Azure OpenAI.</summary>
    public OpenAiResponseBody? ResponseBody { get; set; }
}
