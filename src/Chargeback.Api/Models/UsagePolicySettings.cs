namespace Chargeback.Api.Models;

/// <summary>
/// Runtime usage policy settings for billing cycle and retention behavior.
/// Persisted in Redis to allow runtime updates without redeploying.
/// </summary>
public sealed class UsagePolicySettings
{
    public int BillingCycleStartDay { get; set; } = 1;
    public int AggregatedLogRetentionDays { get; set; } = 30;
    public int TraceRetentionDays { get; set; } = 30;
}
