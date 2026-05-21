namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Per-client access profile that scopes plan/routing/deployment access to an API or operation.
/// Stored in Cosmos configuration container under the access-profile partition.
/// </summary>
public sealed class AccessProfile
{
    public const string PartitionKeyValue = "access-profile";
    public const string GlobalApiId = "_global";
    public const string AllOperations = "_all";

    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ApiId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string? RoutingPolicyId { get; set; }
    public List<string> AllowedDeployments { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string BuildId(string clientAppId, string tenantId, string apiId, string? operationId)
    {
        var normalizedOperationId = string.IsNullOrWhiteSpace(operationId)
            ? AllOperations
            : operationId.Trim();

        return $"ap:{clientAppId.Trim()}:{tenantId.Trim()}:{apiId.Trim()}:{normalizedOperationId}";
    }
}
