using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Cosmos-backed persistence for model routing policies.
/// Partition key: "routing-policy", document id: policy ID.
/// </summary>
public sealed class CosmosRoutingPolicyRepository : CosmosRepositoryBase<ModelRoutingPolicy>
{
    public CosmosRoutingPolicyRepository(ConfigurationContainerProvider provider, ILogger<CosmosRoutingPolicyRepository> logger)
        : base(provider, "routing-policy", logger) { }

    protected override void PrepareForCosmos(ModelRoutingPolicy entity)
        => entity.PartitionKey = "routing-policy";
}
