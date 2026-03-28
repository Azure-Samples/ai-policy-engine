namespace Chargeback.Api.Models;

public sealed class ClientAssignRequest
{
    public string PlanId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    /// <summary>
    /// Optional tenant ID override. If not provided, the tenant ID from the route is used.
    /// </summary>
    public string? TenantId { get; set; }
    /// <summary>
    /// Optional deployment access override. Empty = inherit from plan.
    /// </summary>
    public List<string>? AllowedDeployments { get; set; }
}
