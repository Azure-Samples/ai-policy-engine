namespace Chargeback.Api.Models;

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
}
