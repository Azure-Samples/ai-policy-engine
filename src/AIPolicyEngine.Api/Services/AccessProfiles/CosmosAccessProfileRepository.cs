using System.Net;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using Microsoft.Azure.Cosmos;

namespace AIPolicyEngine.Api.Services.AccessProfiles;

public sealed class CosmosAccessProfileRepository : IAccessProfileRepository
{
    private readonly ConfigurationContainerProvider _provider;
    private readonly ILogger<CosmosAccessProfileRepository> _logger;

    public CosmosAccessProfileRepository(
        ConfigurationContainerProvider provider,
        ILogger<CosmosAccessProfileRepository> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task<AccessProfile?> GetAsync(string profileId, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);

        try
        {
            var response = await _provider.Container.ReadItemAsync<AccessProfile>(
                profileId,
                new PartitionKey(AccessProfile.PartitionKeyValue),
                cancellationToken: ct);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<AccessProfile?> GetForScopeAsync(
        string clientAppId,
        string tenantId,
        string apiId,
        string? operationId,
        CancellationToken ct = default)
        => GetAsync(AccessProfile.BuildId(clientAppId, tenantId, apiId, operationId), ct);

    public async Task<List<AccessProfile>> ListAsync(
        string? clientAppId = null,
        string? tenantId = null,
        string? apiId = null,
        CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);

        var queryText = "SELECT * FROM c WHERE c.partitionKey = @pk";
        var query = new QueryDefinition(queryText)
            .WithParameter("@pk", AccessProfile.PartitionKeyValue);

        if (!string.IsNullOrWhiteSpace(clientAppId))
        {
            queryText += " AND c.clientAppId = @clientAppId";
            query.WithParameter("@clientAppId", clientAppId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            queryText += " AND c.tenantId = @tenantId";
            query.WithParameter("@tenantId", tenantId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(apiId))
        {
            queryText += " AND c.apiId = @apiId";
            query.WithParameter("@apiId", apiId.Trim());
        }

        query = new QueryDefinition(queryText)
            .WithParameter("@pk", AccessProfile.PartitionKeyValue);

        if (!string.IsNullOrWhiteSpace(clientAppId))
            query.WithParameter("@clientAppId", clientAppId.Trim());
        if (!string.IsNullOrWhiteSpace(tenantId))
            query.WithParameter("@tenantId", tenantId.Trim());
        if (!string.IsNullOrWhiteSpace(apiId))
            query.WithParameter("@apiId", apiId.Trim());

        var results = new List<AccessProfile>();
        using var iterator = _provider.Container.GetItemQueryIterator<AccessProfile>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(AccessProfile.PartitionKeyValue) });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        return results
            .OrderBy(p => p.ClientAppId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.TenantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ApiId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.OperationId ?? AccessProfile.AllOperations, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AccessProfile> UpsertAsync(AccessProfile profile, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);
        PrepareForCosmos(profile);

        var response = await _provider.Container.UpsertItemAsync(
            profile,
            new PartitionKey(AccessProfile.PartitionKeyValue),
            cancellationToken: ct);

        _logger.LogInformation("Access profile upserted: {ProfileId}", profile.Id);
        return response.Resource;
    }

    public async Task<bool> DeleteAsync(string profileId, CancellationToken ct = default)
    {
        await _provider.EnsureInitializedAsync(ct);

        try
        {
            await _provider.Container.DeleteItemAsync<AccessProfile>(
                profileId,
                new PartitionKey(AccessProfile.PartitionKeyValue),
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static void PrepareForCosmos(AccessProfile profile)
    {
        profile.ClientAppId = profile.ClientAppId.Trim();
        profile.TenantId = profile.TenantId.Trim();
        profile.ApiId = profile.ApiId.Trim();
        profile.OperationId = string.IsNullOrWhiteSpace(profile.OperationId) ? null : profile.OperationId.Trim();
        profile.PlanId = profile.PlanId.Trim();
        profile.RoutingPolicyId = string.IsNullOrWhiteSpace(profile.RoutingPolicyId) ? null : profile.RoutingPolicyId.Trim();
        profile.AllowedDeployments = (profile.AllowedDeployments ?? [])
            .Where(static deployment => !string.IsNullOrWhiteSpace(deployment))
            .Select(static deployment => deployment.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        profile.Id = AccessProfile.BuildId(profile.ClientAppId, profile.TenantId, profile.ApiId, profile.OperationId);
        profile.PartitionKey = AccessProfile.PartitionKeyValue;
    }
}
