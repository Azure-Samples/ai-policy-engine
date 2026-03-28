namespace Chargeback.Api.Models;

public sealed class QuotasResponse
{
    public List<QuotaData> Quotas { get; set; } = [];
}
