namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Information about an Azure OpenAI deployment from the Foundry resource.
/// </summary>
public sealed class DeploymentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string SkuName { get; set; } = string.Empty;
    public int SkuCapacity { get; set; }
}

public sealed class DeploymentsResponse
{
    public List<DeploymentInfo> Deployments { get; set; } = [];
}
