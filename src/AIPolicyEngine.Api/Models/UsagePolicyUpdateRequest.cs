namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Partial update payload for runtime usage policy settings.
/// </summary>
public sealed class UsagePolicyUpdateRequest
{
    public int? BillingCycleStartDay { get; set; }
    public int? AggregatedLogRetentionDays { get; set; }
    public int? TraceRetentionDays { get; set; }
}
