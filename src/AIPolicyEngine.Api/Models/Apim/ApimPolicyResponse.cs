namespace AIPolicyEngine.Api.Models.Apim;

public sealed class ApimPolicyResponse
{
    public PolicyAssignment? Assignment { get; set; }
    public string CurrentXml { get; set; } = string.Empty;
}
