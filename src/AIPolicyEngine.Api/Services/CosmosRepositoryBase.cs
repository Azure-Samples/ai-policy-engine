using System.Net;
using Microsoft.Azure.Cosmos;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Base class for Cosmos-backed repositories sharing the "configuration" container.
/// Each subclass provides its partition key value and entity preparation logic.
/// </summary>
public abstract class CosmosRepositoryBase<T> : IRepository<T> where T : class
{
    private readonly ConfigurationContainerProvider _provider;
    private readonly string _partitionKeyValue;
    protected readonly ILogger Logger;

    protected CosmosRepositoryBase(
        ConfigurationContainerProvider provider,
        string partitionKeyValue,
        ILogger logger)
    {
        _provider = provider;
        _partitionKeyValue = partitionKeyValue;
        Logger = logger;
    }

    /// <summary>
    /// Set Cosmos-specific fields (Id, PartitionKey) on the entity before upsert.
    /// </summary>
    protected abstract void PrepareForCosmos(T entity);

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);
        try
        {
            var response = await _provider.Container.ReadItemAsync<T>(
                id, new PartitionKey(_partitionKeyValue), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<T>> GetAllAsync(CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk")
            .WithParameter("@pk", _partitionKeyValue);

        var results = new List<T>();
        using var iterator = _provider.Container.GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }
        return results;
    }

    public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);
        PrepareForCosmos(entity);
        var response = await _provider.Container.UpsertItemAsync(
            entity, new PartitionKey(_partitionKeyValue), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);
        try
        {
            await _provider.Container.DeleteItemAsync<T>(
                id, new PartitionKey(_partitionKeyValue), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
