namespace Chargeback.Api.Models;

/// <summary>
/// Response shape for the /api/chargeback/request-summary endpoint.
/// Shows per-client effective request consumption, tier breakdown, and overage costs.
/// </summary>
public sealed class RequestSummaryResponse
{
    public string BillingPeriod { get; set; } = string.Empty;
    public List<RequestSummaryClient> Clients { get; set; } = [];
    public RequestSummaryTotals Totals { get; set; } = new();
}

public sealed class RequestSummaryClient
{
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal TotalEffectiveRequests { get; set; }
    public Dictionary<string, decimal> EffectiveRequestsByTier { get; set; } = new();
    public decimal MultiplierOverageCost { get; set; }
    public long RawRequestCount { get; set; }
}

public sealed class RequestSummaryTotals
{
    public decimal TotalEffectiveRequests { get; set; }
    public Dictionary<string, decimal> EffectiveRequestsByTier { get; set; } = new();
    public decimal TotalMultiplierOverageCost { get; set; }
    public long TotalRawRequests { get; set; }
}
