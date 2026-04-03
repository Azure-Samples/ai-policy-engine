namespace Chargeback.Api.Models;

/// <summary>
/// A billing plan that defines token quotas, rate limits, and pricing.
/// Stored in Redis with key pattern: plan:{planId}
/// </summary>
public sealed class PlanData
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = "plan";
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Monthly subscription rate in USD.</summary>
    public decimal MonthlyRate { get; set; }
    
    /// <summary>Total token allowance per billing period (month).</summary>
    public long MonthlyTokenQuota { get; set; }
    
    /// <summary>Max tokens per minute (0 = unlimited).</summary>
    public int TokensPerMinuteLimit { get; set; }
    
    /// <summary>Max requests per minute (0 = unlimited).</summary>
    public int RequestsPerMinuteLimit { get; set; }
    
    /// <summary>If true, client can exceed quota and gets billed per-token.</summary>
    public bool AllowOverbilling { get; set; }
    
    /// <summary>Cost per 1M tokens when overbilling (USD).</summary>
    public decimal CostPerMillionTokens { get; set; }
    
    /// <summary>If true, MonthlyTokenQuota applies across all deployments combined.
    /// If false, each deployment has its own quota from DeploymentQuotas.</summary>
    public bool RollUpAllDeployments { get; set; } = true;

    /// <summary>Per-deployment token quotas. Only used when RollUpAllDeployments is false.
    /// Key is deploymentId (e.g. "gpt-4o"), value is monthly token limit.</summary>
    public Dictionary<string, long> DeploymentQuotas { get; set; } = new();

    /// <summary>Allowed deployment IDs for this plan. Empty list = all deployments allowed.
    /// When non-empty, only listed deployments can be accessed by clients on this plan.</summary>
    public List<string> AllowedDeployments { get; set; } = [];

    /// <summary>Optional routing policy ID. Null = no model routing for this plan.</summary>
    public string? ModelRoutingPolicyId { get; set; }

    /// <summary>Monthly request quota (0 = unlimited). Used with multiplier billing.</summary>
    public decimal MonthlyRequestQuota { get; set; }

    /// <summary>Cost per request when over quota (USD). 0 = overage not allowed.</summary>
    public decimal OverageRatePerRequest { get; set; }

    /// <summary>If true, billing uses per-request multipliers instead of per-token rates.</summary>
    public bool UseMultiplierBilling { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
