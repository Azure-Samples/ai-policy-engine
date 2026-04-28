namespace AIPolicyEngine.Api.Models;

public sealed class PlanUpdateRequest
{
    public string? Name { get; set; }
    public decimal? MonthlyRate { get; set; }
    public long? MonthlyTokenQuota { get; set; }
    public int? TokensPerMinuteLimit { get; set; }
    public int? RequestsPerMinuteLimit { get; set; }
    public bool? AllowOverbilling { get; set; }
    public decimal? CostPerMillionTokens { get; set; }
    public bool? RollUpAllDeployments { get; set; }
    public Dictionary<string, long>? DeploymentQuotas { get; set; }
    public List<string>? AllowedDeployments { get; set; }

    /// <summary>Optional routing policy ID. Null = no change, empty string = clear.</summary>
    public string? ModelRoutingPolicyId { get; set; }

    /// <summary>Monthly request quota (0 = unlimited).</summary>
    public decimal? MonthlyRequestQuota { get; set; }

    /// <summary>Cost per request when over quota (USD).</summary>
    public decimal? OverageRatePerRequest { get; set; }

    /// <summary>If true, billing uses per-request multipliers.</summary>
    public bool? UseMultiplierBilling { get; set; }
}
