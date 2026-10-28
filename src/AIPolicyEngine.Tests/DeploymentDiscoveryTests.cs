using System.Text.Json;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Tests;

public sealed class DeploymentDiscoveryTests
{
    [Fact]
    public void MapDeployment_PreservesRoutingAndBackendData()
    {
        using var document = JsonDocument.Parse("""
            {
              "name": "gpt-4o-prod",
              "properties": { "model": { "name": "gpt-4o", "version": "2024-11-20" } },
              "sku": { "name": "GlobalStandard", "capacity": 20 }
            }
            """);

        var deployment = DeploymentDiscoveryService.MapDeployment(
            document.RootElement,
            "sub-1",
            "rg-ai",
            "/subscriptions/sub-1/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/foundry-east",
            "foundry-east",
            "https://foundry-east.openai.azure.com/");

        Assert.Equal("gpt-4o-prod", deployment.Name);
        Assert.EndsWith("/deployments/gpt-4o-prod", deployment.Id, StringComparison.Ordinal);
        Assert.Equal("https://foundry-east.openai.azure.com", deployment.Endpoint);
        Assert.Equal("gpt-4o", deployment.Model);
        Assert.Equal(20, deployment.SkuCapacity);
    }
}
