namespace Chargeback.Api.Models;

public sealed class QuotaUpdateRequest
{
    public long MonthlyTokenLimit { get; set; }
    public string? DisplayName { get; set; }
}
