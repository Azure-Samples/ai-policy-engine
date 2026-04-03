using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Cosmos-backed persistence for model pricing configuration.
/// Partition key: "pricing", document id: model ID.
/// </summary>
public sealed class CosmosPricingRepository : CosmosRepositoryBase<ModelPricing>
{
    public CosmosPricingRepository(ConfigurationContainerProvider provider, ILogger<CosmosPricingRepository> logger)
        : base(provider, "pricing", logger) { }

    protected override void PrepareForCosmos(ModelPricing entity)
    {
        entity.Id = entity.ModelId;
        entity.PartitionKey = "pricing";
    }
}
