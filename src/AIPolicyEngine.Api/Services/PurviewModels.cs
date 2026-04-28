using System.Text.Json.Serialization;

namespace AIPolicyEngine.Api.Services;

// ── Output types returned by PurviewGraphClient ────────────────────────────────────────────

/// <summary>Result of a synchronous DLP content check at request time.</summary>
public sealed record PurviewContentCheckResult
{
    public bool IsBlocked { get; init; }
    public string? BlockMessage { get; init; }
}

/// <summary>Claims decoded from the JWT acquired by <see cref="PurviewGraphClient"/>.</summary>
internal sealed class PurviewTokenInfo
{
    public string UserId   { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    /// <summary>True when the JWT was issued to a human user (idtyp == "user").</summary>
    public bool IsUserToken { get; init; }
}

/// <summary>Result of a <c>processContent</c> DLP evaluation.</summary>
internal sealed class PurviewProcessContentResult
{
    public bool ShouldBlock { get; init; }
    public IReadOnlyList<string> PolicyActions { get; init; } = [];
}

/// <summary>Result of a <c>protectionScopes/compute</c> check.</summary>
internal sealed class PurviewProtectionScopesResult
{
    /// <summary>When false, skip full processContent evaluation for this tenant/user.</summary>
    public bool ShouldProcess { get; init; }
    public string? ScopeIdentifier { get; init; }
    public string? ExecutionMode   { get; init; }
}

// ── Input types passed to PurviewGraphClient by PurviewAuditService ───────────────────────

/// <summary>One message direction to emit to the Graph API.</summary>
internal sealed class PurviewContentRequest
{
    public string UserId            { get; init; } = string.Empty;
    public string TenantId          { get; init; } = string.Empty;
    public string CorrelationId     { get; init; } = string.Empty;
    public string MessageText       { get; init; } = string.Empty;
    public string MessageId         { get; init; } = string.Empty;
    public string UserDisplayName   { get; init; } = string.Empty;
    /// <summary>"UploadText" (prompt) or "DownloadText" (completion).</summary>
    public string Activity          { get; init; } = "UploadText";
}

/// <summary>Parameters for a <c>protectionScopes/compute</c> call.</summary>
internal sealed class PurviewProtectionScopesRequest
{
    public string UserId        { get; init; } = string.Empty;
    public string TenantId      { get; init; } = string.Empty;
    public string Activity      { get; init; } = "UploadText";
    public string CorrelationId { get; init; } = string.Empty;
}

// ── Graph API JSON DTOs (camelCase + @odata.type conventions) ─────────────────────────────

internal sealed class GraphLocation
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "microsoft.graph.policyLocationApplication";

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

internal sealed class GraphOsSpecs
{
    [JsonPropertyName("operatingSystemPlatform")]
    public string OperatingSystemPlatform { get; init; } = "Unknown";

    [JsonPropertyName("operatingSystemVersion")]
    public string OperatingSystemVersion { get; init; } = "Unknown";
}

internal sealed class GraphDeviceMetadata
{
    [JsonPropertyName("operatingSystemSpecifications")]
    public GraphOsSpecs OperatingSystemSpecifications { get; init; } = new();
}

internal sealed class GraphAppMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";
}

internal sealed class GraphProtectedAppMetadata
{
    [JsonPropertyName("applicationLocation")]
    public GraphLocation ApplicationLocation { get; init; } = new();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";
}

internal sealed class GraphTextItem
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "microsoft.graph.textItem";

    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;
}

internal sealed class GraphConversationMetadata
{
    [JsonPropertyName("content")]
    public GraphTextItem Content { get; init; } = new();

    [JsonPropertyName("identifier")]
    public string Identifier { get; init; } = string.Empty;

    [JsonPropertyName("isFinal")]
    public bool IsFinal { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("sequenceNumber")]
    public long SequenceNumber { get; init; }
}

internal sealed class GraphActivityMetadata
{
    [JsonPropertyName("activity")]
    public string Activity { get; init; } = "UploadText";
}

internal sealed class GraphContentToProcess
{
    [JsonPropertyName("conversationData")]
    public List<GraphConversationMetadata> ConversationData { get; init; } = [];

    [JsonPropertyName("activityMetadata")]
    public GraphActivityMetadata ActivityMetadata { get; init; } = new();

    [JsonPropertyName("deviceMetadata")]
    public GraphDeviceMetadata DeviceMetadata { get; init; } = new();

    [JsonPropertyName("integratedAppMetadata")]
    public GraphAppMetadata IntegratedAppMetadata { get; init; } = new();

    [JsonPropertyName("protectedAppMetadata")]
    public GraphProtectedAppMetadata ProtectedAppMetadata { get; init; } = new();
}

internal sealed class GraphProcessContentRequest
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("contentToProcess")]
    public GraphContentToProcess ContentToProcess { get; init; } = new();

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;
}

internal sealed class GraphProcessContentResponse
{
    [JsonPropertyName("policyActions")]
    public List<string>? PolicyActions { get; init; }
}

internal sealed class GraphProtectionScopesRequest
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("activities")]
    public string Activities { get; init; } = "UploadText";

    [JsonPropertyName("locations")]
    public List<GraphLocation> Locations { get; init; } = [];

    [JsonPropertyName("deviceMetadata")]
    public GraphDeviceMetadata DeviceMetadata { get; init; } = new();

    [JsonPropertyName("integratedAppMetadata")]
    public GraphAppMetadata IntegratedAppMetadata { get; init; } = new();

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;
}

internal sealed class GraphProtectionScopesResponse
{
    [JsonPropertyName("shouldProcess")]
    public bool ShouldProcess { get; init; }

    [JsonPropertyName("scopeIdentifier")]
    public string? ScopeIdentifier { get; init; }

    [JsonPropertyName("executionMode")]
    public string? ExecutionMode { get; init; }
}

internal sealed class GraphContentActivitiesRequest
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("contentToProcess")]
    public GraphContentToProcess ContentToProcess { get; init; } = new();

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;
}
