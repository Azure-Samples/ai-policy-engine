using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Cosmos-backed persistence for the singleton usage policy settings document.
/// Partition key: "settings", document id: "usage-policy".
/// </summary>
public sealed class CosmosUsagePolicyRepository : CosmosRepositoryBase<UsagePolicySettings>
{
    public CosmosUsagePolicyRepository(ConfigurationContainerProvider provider, ILogger<CosmosUsagePolicyRepository> logger)
        : base(provider, "settings", logger) { }

    protected override void PrepareForCosmos(UsagePolicySettings entity)
    {
        entity.Id = "usage-policy";
        entity.PartitionKey = "settings";
    }
}
