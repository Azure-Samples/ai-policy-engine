using Chargeback.Api.Models;
using Microsoft.Azure.Cosmos;

namespace Chargeback.Api.Services;

/// <summary>
/// Cosmos DB-backed audit store for durable financial record-keeping.
/// Ensures database and containers exist on first use.
/// </summary>
public sealed class AuditStore : IAuditStore
{
    private const string DatabaseName = "chargeback";
    private const string AuditLogsContainer = "audit-logs";
    private const string BillingSummariesContainer = "billing-summaries";
    private const int DefaultTtlSeconds = 94608000; // 36 months

    private readonly CosmosClient _cosmosClient;
    private readonly ILogger<AuditStore> _logger;
    private volatile bool _initialized;

    public AuditStore(CosmosClient cosmosClient, ILogger<AuditStore> logger)
    {
        _cosmosClient = cosmosClient;
        _logger = logger;
    }

    private Container AuditLogs => _cosmosClient.GetContainer(DatabaseName, AuditLogsContainer);
    private Container BillingSummaries => _cosmosClient.GetContainer(DatabaseName, BillingSummariesContainer);

    /// <summary>
    /// Ensures the Cosmos database and containers exist. Called once on first write.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        try
        {
            var dbResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: ct);
            var database = dbResponse.Database;

            await database.CreateContainerIfNotExistsAsync(new ContainerProperties
            {
                Id = AuditLogsContainer,
                PartitionKeyPath = "/customerKey",
                DefaultTimeToLive = DefaultTtlSeconds
            }, cancellationToken: ct);

            await database.CreateContainerIfNotExistsAsync(new ContainerProperties
            {
                Id = BillingSummariesContainer,
                PartitionKeyPath = "/customerKey",
                DefaultTimeToLive = DefaultTtlSeconds
            }, cancellationToken: ct);

            _initialized = true;
            _logger.LogInformation("Cosmos DB database and containers verified");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Cosmos DB database/containers");
            throw;
        }
    }

    public async Task WriteBatchAsync(IReadOnlyList<AuditLogDocument> documents, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var tasks = new List<Task>(documents.Count);
        foreach (var doc in documents)
        {
            tasks.Add(AuditLogs.CreateItemAsync(
                doc,
                new PartitionKey(doc.CustomerKey),
                cancellationToken: ct));
        }

        await Task.WhenAll(tasks);
    }

    public async Task UpsertBillingSummariesAsync(IReadOnlyList<AuditLogItem> items, CancellationToken ct = default)
    {
        // Group items by customer+deployment+period to minimize upserts
        var groups = items.GroupBy(i => (i.ClientAppId, i.TenantId, i.DeploymentId, Period: $"{i.Timestamp:yyyy-MM}"));

        foreach (var group in groups)
        {
            var (clientAppId, tenantId, deploymentId, period) = group.Key;
            var customerKey = $"{clientAppId}:{tenantId}";
            var summaryId = $"{clientAppId}:{tenantId}:{deploymentId}:{period}";
            var partitionKey = new PartitionKey(customerKey);

            BillingSummaryDocument summary;
            try
            {
                var existing = await BillingSummaries.ReadItemAsync<BillingSummaryDocument>(
                    summaryId, partitionKey, cancellationToken: ct);
                summary = existing.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var first = group.First();
                summary = new BillingSummaryDocument
                {
                    Id = summaryId,
                    CustomerKey = customerKey,
                    ClientAppId = clientAppId,
                    DisplayName = first.DisplayName,
                    TenantId = first.TenantId,
                    Audience = first.Audience,
                    DeploymentId = deploymentId,
                    Model = first.Model,
                    BillingPeriod = period,
                };
            }

            foreach (var item in group)
            {
                summary.PromptTokens += item.PromptTokens;
                summary.CompletionTokens += item.CompletionTokens;
                summary.TotalTokens += item.TotalTokens;
                summary.ImageTokens += item.ImageTokens;
                summary.RequestCount++;

                if (decimal.TryParse(item.CostToUs, out var costToUs))
                    summary.CostToUs += costToUs;
                if (decimal.TryParse(item.CostToCustomer, out var costToCustomer))
                    summary.CostToCustomer += costToCustomer;

                if (item.IsOverbilled)
                    summary.IsOverbilled = true;

                // Keep display name fresh
                if (!string.IsNullOrEmpty(item.DisplayName))
                    summary.DisplayName = item.DisplayName;
            }

            summary.UpdatedAt = DateTime.UtcNow;

            await BillingSummaries.UpsertItemAsync(summary, partitionKey, cancellationToken: ct);
        }
    }

    public async Task<List<BillingSummaryDocument>> GetBillingSummariesAsync(
        string billingPeriod, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.billingPeriod = @period")
            .WithParameter("@period", billingPeriod);

        return await QueryAllAsync<BillingSummaryDocument>(BillingSummaries, query, ct);
    }

    public async Task<List<AuditLogDocument>> GetClientAuditLogsAsync(
        string customerKey, string billingPeriod, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.customerKey = @customerKey AND c.billingPeriod = @period ORDER BY c.timestamp DESC")
            .WithParameter("@customerKey", customerKey)
            .WithParameter("@period", billingPeriod);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(customerKey) };
        return await QueryAllAsync<AuditLogDocument>(AuditLogs, query, ct, options);
    }

    public async Task<List<ExportPeriod>> GetAvailablePeriodsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var query = new QueryDefinition(
            "SELECT DISTINCT VALUE c.billingPeriod FROM c");

        var periods = await QueryAllAsync<string>(BillingSummaries, query, ct);

        return periods
            .Where(p => p.Length == 7 && p[4] == '-')
            .Select(p => new ExportPeriod
            {
                Year = int.Parse(p[..4]),
                Month = int.Parse(p[5..])
            })
            .OrderByDescending(p => p.Year)
            .ThenByDescending(p => p.Month)
            .ToList();
    }

    public async Task<List<ExportClient>> GetClientsForPeriodAsync(
        string billingPeriod, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var query = new QueryDefinition(
            "SELECT DISTINCT c.clientAppId, c.tenantId, c.displayName FROM c WHERE c.billingPeriod = @period")
            .WithParameter("@period", billingPeriod);

        var results = await QueryAllAsync<BillingSummaryDocument>(BillingSummaries, query, ct);

        return results
            .GroupBy(r => $"{r.ClientAppId}:{r.TenantId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExportClient
            {
                ClientAppId = g.First().ClientAppId,
                TenantId = g.First().TenantId,
                DisplayName = g.First().DisplayName
            })
            .OrderBy(c => c.DisplayName)
            .ToList();
    }

    private async Task<List<T>> QueryAllAsync<T>(
        Container container,
        QueryDefinition query,
        CancellationToken ct,
        QueryRequestOptions? options = null)
    {
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }
        return results;
    }
}
