namespace AIPolicyEngine.Api.Models;

public sealed class AccessProfileCreateRequest
{
    public string ClientAppId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ApiId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string? RoutingPolicyId { get; set; }
    public List<string>? AllowedDeployments { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AccessProfileUpdateRequest
{
    public string? PlanId { get; set; }
    public string? RoutingPolicyId { get; set; }
    public List<string>? AllowedDeployments { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class BulkAccessProfilesRequest
{
    public List<AccessProfileCreateRequest> Profiles { get; set; } = [];
}
