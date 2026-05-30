namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Log data received from the non-AI REST APIM outbound policy.
/// Used to increment per-API usage counters on ClientPlanAssignment.
/// </summary>
public sealed class RestLogIngestRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientAppId { get; set; } = string.Empty;
    public string CustomerKey { get; set; } = string.Empty;
    public string ApiId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string? AccessProfileId { get; set; }
    public string? PlanId { get; set; }
    public string? RequestPath { get; set; }
    public int StatusCode { get; set; }
    public long LatencyMs { get; set; }
    public string? CorrelationId { get; set; }
}
