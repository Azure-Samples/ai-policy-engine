namespace Chargeback.Api.Models;

// Keep backward compatibility — alias for any remaining references
public sealed class QuotaData
{
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long MonthlyTokenLimit { get; set; }
    public long CurrentUsage { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
