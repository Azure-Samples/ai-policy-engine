namespace Chargeback.Api.Models;

public sealed class ClientUsageResponse
{
    public ClientPlanAssignment? Assignment { get; set; }
    public PlanData? Plan { get; set; }
    public List<LogEntry> Logs { get; set; } = [];
    public Dictionary<string, long> UsageByModel { get; set; } = [];
    public long CurrentTpm { get; set; }
    public long CurrentRpm { get; set; }
    public decimal TotalCostToUs { get; set; }
    public decimal TotalCostToCustomer { get; set; }

    // Request-based usage (multiplier billing)
    public decimal CurrentPeriodRequests { get; set; }
    public decimal OverbilledRequests { get; set; }
    public Dictionary<string, decimal> RequestsByTier { get; set; } = new();
    public decimal MonthlyRequestQuota { get; set; }

    /// <summary>Request utilization percentage (0–100). -1 = unlimited quota.</summary>
    public decimal RequestUtilizationPercent { get; set; }
}
