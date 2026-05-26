namespace AIPolicyEngine.Api.Models.Apim;

public sealed class ApplyPolicyAcceptedResponse
{
    public string AssignmentId { get; set; } = string.Empty;
    public string Status { get; set; } = PolicyAssignmentStatuses.Applying;
}
