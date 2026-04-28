using Microsoft.Azure.Cosmos;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Shared provider for the CosmosDB "configuration" container.
/// Ensures the database and container exist before first use (idempotent).
/// </summary>
public sealed class ConfigurationContainerProvider
{
    private const string DatabaseName = "aipolicy";
    private const string ContainerName = "configuration";

    private readonly CosmosClient _cosmosClient;
    private readonly ILogger<ConfigurationContainerProvider> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public ConfigurationContainerProvider(CosmosClient cosmosClient, ILogger<ConfigurationContainerProvider> logger)
    {
        _cosmosClient = cosmosClient;
        _logger = logger;
    }

    public Container Container => _cosmosClient.GetContainer(DatabaseName, ContainerName);

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var dbResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: ct);
            await dbResponse.Database.CreateContainerIfNotExistsAsync(new ContainerProperties
            {
                Id = ContainerName,
                PartitionKeyPath = "/partitionKey"
            }, cancellationToken: ct);

            _initialized = true;
            _logger.LogInformation("Cosmos DB configuration container verified");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Cosmos DB configuration container");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
