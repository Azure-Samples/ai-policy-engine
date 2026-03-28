namespace Chargeback.Api.Models;

/// <summary>
/// Links a client application + tenant combination to a billing plan.
/// A "Customer" is uniquely identified by the clientAppId:tenantId pair.
/// Stored in Redis with key pattern: client:{clientAppId}:{tenantId}
/// </summary>
public sealed class ClientPlanAssignment
{
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CurrentPeriodStart { get; set; } = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    public long CurrentPeriodUsage { get; set; }
    public long OverbilledTokens { get; set; }

    /// <summary>Token usage broken down by deployment ID for the current period.</summary>
    public Dictionary<string, long> DeploymentUsage { get; set; } = new();

    /// <summary>Allowed deployment IDs for this customer. Empty list = inherit from plan.
    /// When non-empty, overrides the plan's AllowedDeployments list.</summary>
    public List<string> AllowedDeployments { get; set; } = [];

    /// <summary>Current requests per minute (populated dynamically, not persisted).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long CurrentRpm { get; set; }

    /// <summary>Current tokens per minute (populated dynamically, not persisted).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long CurrentTpm { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
