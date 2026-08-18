namespace AIPolicyEngine.Api.Models;

/// <summary>
/// Information about an Azure OpenAI deployment from the Foundry resource.
/// </summary>
public sealed class DeploymentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string SkuName { get; set; } = string.Empty;
    public int SkuCapacity { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
}

public sealed class DeploymentsResponse
{
    public List<DeploymentInfo> Deployments { get; set; } = [];
}

public sealed class FoundryOnboardingResponse
{
    public int ResourcesOnboarded { get; set; }
    public int DeploymentsDiscovered { get; set; }
    public int PoliciesQueuedForReapply { get; set; }
}
