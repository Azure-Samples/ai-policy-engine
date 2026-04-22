using System.Net.Http.Headers;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using Azure.Identity;
using Azure.Core;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Services;

public interface IDeploymentDiscoveryService
{
    Task<List<DeploymentInfo>> GetDeploymentsAsync(CancellationToken ct = default);
}

public sealed class DeploymentDiscoveryService : IDeploymentDiscoveryService
{
    private const string CacheKey = "deployments:available";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeploymentDiscoveryService> _logger;
    private readonly TokenCredential _credential;

    public DeploymentDiscoveryService(
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        ILogger<DeploymentDiscoveryService> logger)
    {
        _redis = redis;
        _configuration = configuration;
        _logger = logger;
        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzureCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
        });
    }

    public async Task<List<DeploymentInfo>> GetDeploymentsAsync(CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();

        // Check cache first
        var cached = await db.StringGetAsync(CacheKey);
        if (cached.HasValue)
        {
            try
            {
                var cachedList = JsonSerializer.Deserialize<List<DeploymentInfo>>((string)cached!, JsonConfig.Default);
                if (cachedList is not null) return cachedList;
            }
            catch (JsonException) { }
        }

        var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"];
        var resourceGroup = _configuration["AZURE_RESOURCE_GROUP"];
        var endpoint = _configuration["AZURE_AI_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(resourceGroup) || string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, or AZURE_AI_ENDPOINT not configured — returning empty deployment list");
            return [];
        }

        // Derive the account name from the endpoint URL (e.g. https://aipolicy-ai.cognitiveservices.azure.com/)
        var uri = new Uri(endpoint);
        var accountName = uri.Host.Split('.')[0];

        try
        {
            // Use the management plane API to list deployments (works for AI Services multi-service accounts)
            var tokenResult = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]), ct);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

            var apiUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CognitiveServices/accounts/{accountName}/deployments?api-version=2024-10-01";
            var response = await httpClient.GetAsync(apiUrl, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var deployments = new List<DeploymentInfo>();
            if (doc.RootElement.TryGetProperty("value", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var model = "";
                    var modelVersion = "";
                    if (item.TryGetProperty("properties", out var props) && props.TryGetProperty("model", out var modelObj))
                    {
                        model = modelObj.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                        modelVersion = modelObj.TryGetProperty("version", out var mv) ? mv.GetString() ?? "" : "";
                    }

                    var skuName = "";
                    var skuCapacity = 0;
                    if (item.TryGetProperty("sku", out var sku))
                    {
                        skuName = sku.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                        skuCapacity = sku.TryGetProperty("capacity", out var sc) && sc.TryGetInt32(out var cap) ? cap : 0;
                    }

                    deployments.Add(new DeploymentInfo
                    {
                        Id = name,
                        Model = model,
                        ModelVersion = modelVersion,
                        SkuName = skuName,
                        SkuCapacity = skuCapacity
                    });
                }
            }

            // Cache the result
            var cacheValue = JsonSerializer.Serialize(deployments, JsonConfig.Default);
            await db.StringSetAsync(CacheKey, cacheValue, CacheTtl);

            _logger.LogInformation("Fetched {Count} deployments from Azure AI Services", deployments.Count);
            return deployments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch deployments from Azure AI Services");
            return [];
        }
    }
}
