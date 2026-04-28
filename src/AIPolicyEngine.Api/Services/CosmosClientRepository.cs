using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Cosmos-backed persistence for client plan assignments.
/// Partition key: "client", document id: "{clientAppId}:{tenantId}".
/// </summary>
public sealed class CosmosClientRepository : CosmosRepositoryBase<ClientPlanAssignment>
{
    public CosmosClientRepository(ConfigurationContainerProvider provider, ILogger<CosmosClientRepository> logger)
        : base(provider, "client", logger) { }

    protected override void PrepareForCosmos(ClientPlanAssignment entity)
    {
        entity.Id = $"{entity.ClientAppId}:{entity.TenantId}";
        entity.PartitionKey = "client";
    }
}
