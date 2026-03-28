namespace Chargeback.Api.Models;

/// <summary>
/// Available export period returned by the available-periods endpoint.
/// </summary>
public sealed class ExportPeriod
{
    public int Year { get; set; }
    public int Month { get; set; }
}

/// <summary>
/// Client summary for a given billing period (for the period selector UI).
/// A "Customer" is the clientAppId:tenantId combination.
/// </summary>
public sealed class ExportClient
{
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Response from the available-periods endpoint.
/// </summary>
public sealed class ExportPeriodsResponse
{
    public List<ExportPeriod> Periods { get; set; } = [];
    public ExportPeriod CurrentPeriod { get; set; } = new();
    public List<ExportClient> Clients { get; set; } = [];
}
