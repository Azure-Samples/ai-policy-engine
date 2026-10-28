using System.Net.Http.Headers;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Services;

public interface IDeploymentDiscoveryService
{
    Task<List<DeploymentInfo>> GetDeploymentsAsync(CancellationToken ct = default);
    Task<DeploymentInfo?> ResolveAsync(string deploymentId, CancellationToken ct = default);
    Task<int> OnboardAsync(CancellationToken ct = default);
}

public sealed class DeploymentDiscoveryService : IDeploymentDiscoveryService
{
    private const string CacheKey = "deployments:available:v2";
    private const string ManagementScope = "https://management.azure.com/.default";
    private const string CognitiveServicesUserRoleId = "a97b65f3-24c7-4388-baec-2e87135dc908";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _configuration;
    private readonly FoundryDiscoveryOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeploymentDiscoveryService> _logger;
    private readonly TokenCredential _credential;

    public DeploymentDiscoveryService(
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        IOptions<FoundryDiscoveryOptions> options,
        HttpClient httpClient,
        ILogger<DeploymentDiscoveryService> logger)
    {
        _redis = redis;
        _configuration = configuration;
        _options = options.Value;
        _httpClient = httpClient;
        _logger = logger;
        _credential = CreateCredential(_options);
    }

    public async Task<List<DeploymentInfo>> GetDeploymentsAsync(CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var cached = await db.StringGetAsync(CacheKey);
        if (cached.HasValue)
        {
            try
            {
                var cachedList = JsonSerializer.Deserialize<List<DeploymentInfo>>((string)cached!, JsonConfig.Default);
                if (cachedList is not null)
                    return cachedList;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ignoring invalid Foundry deployment cache");
            }
        }

        var subscriptionIds = await GetSubscriptionIdsAsync(ct);
        var deployments = new List<DeploymentInfo>();
        var resources = await DiscoverResourcesAsync(subscriptionIds, ct);
        foreach (var resource in resources)
        {
            var deploymentsUrl = $"https://management.azure.com{resource.ResourceId}/deployments?api-version=2024-10-01";
            await foreach (var deployment in GetValuesAsync(deploymentsUrl, ct))
            {
                deployments.Add(MapDeployment(
                    deployment,
                    resource.SubscriptionId,
                    resource.ResourceGroup,
                    resource.ResourceId,
                    resource.ResourceName,
                    resource.Endpoint));
            }
        }

        deployments = deployments
            .OrderBy(item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await db.StringSetAsync(CacheKey, JsonSerializer.Serialize(deployments, JsonConfig.Default), CacheTtl);
        _logger.LogInformation(
            "Discovered {DeploymentCount} deployments across {SubscriptionCount} subscription(s)",
            deployments.Count,
            subscriptionIds.Count);
        return deployments;
    }

    public async Task<DeploymentInfo?> ResolveAsync(string deploymentId, CancellationToken ct = default)
    {
        var deployments = await GetDeploymentsAsync(ct);
        var exact = deployments.FirstOrDefault(item =>
            string.Equals(item.Id, deploymentId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var byName = deployments
            .Where(item => string.Equals(item.Name, deploymentId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public async Task<int> OnboardAsync(CancellationToken ct = default)
    {
        var resources = await DiscoverResourcesAsync(await GetSubscriptionIdsAsync(ct), ct);
        if (resources.Count == 0)
            return 0;

        var apimResourceId = _configuration["Apim:ResourceId"];
        if (string.IsNullOrWhiteSpace(apimResourceId))
            throw new InvalidOperationException("Apim:ResourceId must be configured before Foundry resources can be onboarded.");

        var apim = await GetJsonAsync(
            $"https://management.azure.com{apimResourceId}?api-version=2024-05-01",
            ct);
        var principalId = apim.GetProperty("identity").GetProperty("principalId").GetString();
        if (string.IsNullOrWhiteSpace(principalId))
            throw new InvalidOperationException("The configured APIM service does not have a managed identity.");

        foreach (var resource in resources)
        {
            var roleDefinitionId = $"/subscriptions/{resource.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{CognitiveServicesUserRoleId}";
            var assignmentId = GuidFrom($"{resource.ResourceId}|{principalId}|{CognitiveServicesUserRoleId}");
            var url = $"https://management.azure.com{resource.ResourceId}/providers/Microsoft.Authorization/roleAssignments/{assignmentId}?api-version=2022-04-01";
            await PutJsonAsync(url, new
            {
                properties = new
                {
                    roleDefinitionId,
                    principalId,
                    principalType = "ServicePrincipal"
                }
            }, ct);
        }

        return resources.Count;
    }

    private async Task<List<FoundryResource>> DiscoverResourcesAsync(
        IReadOnlyCollection<string> subscriptionIds,
        CancellationToken ct)
    {
        var resources = new List<FoundryResource>();
        foreach (var subscriptionId in subscriptionIds)
        {
            var accountsUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CognitiveServices/accounts?api-version=2024-10-01";
            await foreach (var account in GetValuesAsync(accountsUrl, ct))
            {
                var kind = account.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() : null;
                if (kind is not ("OpenAI" or "AIServices" or "CognitiveServices"))
                    continue;

                var resourceId = account.GetProperty("id").GetString()!;
                var endpoint = account.GetProperty("properties").TryGetProperty("endpoint", out var endpointProperty)
                    ? endpointProperty.GetString() ?? string.Empty
                    : string.Empty;
                resources.Add(new FoundryResource(
                    subscriptionId,
                    GetResourceGroup(resourceId),
                    resourceId,
                    account.GetProperty("name").GetString()!,
                    endpoint));
            }
        }

        return resources;
    }

    internal static DeploymentInfo MapDeployment(
        JsonElement deployment,
        string subscriptionId,
        string resourceGroup,
        string resourceId,
        string resourceName,
        string endpoint)
    {
        var name = deployment.GetProperty("name").GetString() ?? string.Empty;
        var properties = deployment.GetProperty("properties");
        var model = properties.GetProperty("model");
        var sku = deployment.TryGetProperty("sku", out var skuProperty) ? skuProperty : default;

        return new DeploymentInfo
        {
            Id = $"{resourceId}/deployments/{name}",
            Name = name,
            Model = model.TryGetProperty("name", out var modelName) ? modelName.GetString() ?? string.Empty : string.Empty,
            ModelVersion = model.TryGetProperty("version", out var modelVersion) ? modelVersion.GetString() ?? string.Empty : string.Empty,
            SkuName = sku.ValueKind == JsonValueKind.Object && sku.TryGetProperty("name", out var skuName) ? skuName.GetString() ?? string.Empty : string.Empty,
            SkuCapacity = sku.ValueKind == JsonValueKind.Object && sku.TryGetProperty("capacity", out var capacity) && capacity.TryGetInt32(out var value) ? value : 0,
            Endpoint = endpoint.TrimEnd('/'),
            ResourceId = resourceId,
            ResourceName = resourceName,
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId
        };
    }

    private async Task<List<string>> GetSubscriptionIdsAsync(CancellationToken ct)
    {
        var configured = _options.SubscriptionIds
            .Concat([_configuration["AZURE_SUBSCRIPTION_ID"] ?? string.Empty])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!_options.DiscoverAllSubscriptions)
            return configured;

        var subscriptions = new List<string>();
        await foreach (var subscription in GetValuesAsync(
                           "https://management.azure.com/subscriptions?api-version=2022-12-01",
                           ct))
        {
            if (subscription.TryGetProperty("state", out var state) &&
                !string.Equals(state.GetString(), "Enabled", StringComparison.OrdinalIgnoreCase))
                continue;

            if (subscription.TryGetProperty("subscriptionId", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                subscriptions.Add(id.GetString()!);
        }

        return subscriptions;
    }

    private async IAsyncEnumerable<JsonElement> GetValuesAsync(
        string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var document = JsonDocument.Parse(await GetStringAsync(url, ct));
            if (document.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var value in values.EnumerateArray())
                    yield return value.Clone();
            }

            url = document.RootElement.TryGetProperty("nextLink", out var nextLink)
                ? nextLink.GetString() ?? string.Empty
                : string.Empty;
        }
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await GetStringAsync(url, ct));
        return document.RootElement.Clone();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, url, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task PutJsonAsync(string url, object body, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, url, ct);
        request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), ct);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private static TokenCredential CreateCredential(FoundryDiscoveryOptions options)
    {
        var configured = !string.IsNullOrWhiteSpace(options.TenantId) ||
                         !string.IsNullOrWhiteSpace(options.ClientId) ||
                         !string.IsNullOrWhiteSpace(options.ClientSecret);
        if (configured)
        {
            if (string.IsNullOrWhiteSpace(options.TenantId) ||
                string.IsNullOrWhiteSpace(options.ClientId) ||
                string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new InvalidOperationException("Foundry service principal configuration requires TenantId, ClientId, and ClientSecret.");

            return new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzureCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true
        });
    }

    private static string GetResourceGroup(string resourceId)
    {
        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, value => string.Equals(value, "resourceGroups", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : string.Empty;
    }

    private static Guid GuidFrom(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record FoundryResource(
        string SubscriptionId,
        string ResourceGroup,
        string ResourceId,
        string ResourceName,
        string Endpoint);
}
