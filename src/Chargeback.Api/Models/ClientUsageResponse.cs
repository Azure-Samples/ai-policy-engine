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
}
