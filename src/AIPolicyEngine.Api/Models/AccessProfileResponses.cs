namespace AIPolicyEngine.Api.Models;

public sealed class AccessProfilesResponse
{
    public List<AccessProfile> Profiles { get; set; } = [];
}

public sealed class BulkAccessProfilesResponse
{
    public int Created { get; set; }
    public List<BulkAccessProfileFailure> Failed { get; set; } = [];
}

public sealed class BulkAccessProfileFailure
{
    public int Index { get; set; }
    public string Error { get; set; } = string.Empty;
    public string? ProfileId { get; set; }
}

public sealed class ResolvedAccessProfile
{
    public string PlanId { get; set; } = string.Empty;
    public string? RoutingPolicyId { get; set; }
    public List<string> AllowedDeployments { get; set; } = [];
    public bool Blocked { get; set; }
    public string? AccessProfileId { get; set; }
    public string? SourceProfileId
    {
        get => AccessProfileId;
        set => AccessProfileId = value;
    }
}
