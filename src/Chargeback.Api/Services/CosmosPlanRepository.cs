using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Cosmos-backed persistence for billing plans.
/// Partition key: "plan", document id: plan ID.
/// </summary>
public sealed class CosmosPlanRepository : CosmosRepositoryBase<PlanData>
{
    public CosmosPlanRepository(ConfigurationContainerProvider provider, ILogger<CosmosPlanRepository> logger)
        : base(provider, "plan", logger) { }

    protected override void PrepareForCosmos(PlanData entity)
        => entity.PartitionKey = "plan";
}
