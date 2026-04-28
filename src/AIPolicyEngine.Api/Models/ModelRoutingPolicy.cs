namespace AIPolicyEngine.Api.Models;

/// <summary>
/// A routing policy that maps requested deployments to actual Foundry deployments.
/// Stored in Cosmos with partition key "routing-policy".
/// </summary>
public sealed class ModelRoutingPolicy
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = "routing-policy";
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<RouteRule> Rules { get; set; } = [];
    public RoutingBehavior DefaultBehavior { get; set; } = RoutingBehavior.Passthrough;

    /// <summary>Optional fallback deployment when DefaultBehavior is Passthrough and no rule matches.</summary>
    public string? FallbackDeployment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
