using System.Text.Json;
using Chargeback.Api.Models;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

/// <summary>
/// Warms the Redis cache from Cosmos on startup (runs after migration).
/// If Redis is unavailable, logs a warning but does not fail startup.
/// If Cosmos is unavailable, fails startup — no source of truth means no service.
/// </summary>
public sealed class CacheWarmingService : IHostedService
{
    private readonly CosmosPlanRepository _planRepo;
    private readonly CosmosClientRepository _clientRepo;
    private readonly CosmosPricingRepository _pricingRepo;
    private readonly CosmosUsagePolicyRepository _usagePolicyRepo;
    private readonly CosmosRoutingPolicyRepository _routingPolicyRepo;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CacheWarmingService> _logger;

    public CacheWarmingService(
        CosmosPlanRepository planRepo,
        CosmosClientRepository clientRepo,
        CosmosPricingRepository pricingRepo,
        CosmosUsagePolicyRepository usagePolicyRepo,
        CosmosRoutingPolicyRepository routingPolicyRepo,
        IConnectionMultiplexer redis,
        ILogger<CacheWarmingService> logger)
    {
        _planRepo = planRepo;
        _clientRepo = clientRepo;
        _pricingRepo = pricingRepo;
        _usagePolicyRepo = usagePolicyRepo;
        _routingPolicyRepo = routingPolicyRepo;
        _redis = redis;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Cache warming starting — loading configuration from Cosmos");

        // Cosmos must be available. If it throws, startup fails (by design).
        var plans = await _planRepo.GetAllAsync(ct);
        var clients = await _clientRepo.GetAllAsync(ct);
        var pricing = await _pricingRepo.GetAllAsync(ct);
        var usagePolicy = await _usagePolicyRepo.GetAsync("usage-policy", ct);
        var routingPolicies = await _routingPolicyRepo.GetAllAsync(ct);

        try
        {
            var db = _redis.GetDatabase();

            foreach (var plan in plans)
            {
                var json = JsonSerializer.Serialize(plan, JsonConfig.Default);
                await db.StringSetAsync(RedisKeys.Plan(plan.Id), json);
            }

            foreach (var client in clients)
            {
                var json = JsonSerializer.Serialize(client, JsonConfig.Default);
                await db.StringSetAsync(RedisKeys.Client(client.ClientAppId, client.TenantId), json);
            }

            foreach (var model in pricing)
            {
                var json = JsonSerializer.Serialize(model, JsonConfig.Default);
                await db.StringSetAsync(RedisKeys.Pricing(model.ModelId), json);
            }

            if (usagePolicy is not null)
            {
                var json = JsonSerializer.Serialize(usagePolicy, JsonConfig.Default);
                await db.StringSetAsync(RedisKeys.UsagePolicySettings, json);
            }

            foreach (var policy in routingPolicies)
            {
                var json = JsonSerializer.Serialize(policy, JsonConfig.Default);
                await db.StringSetAsync(RedisKeys.RoutingPolicy(policy.Id), json);
            }

            _logger.LogInformation(
                "Cache warming complete: {Plans} plans, {Clients} clients, {Pricing} pricing models, {RoutingPolicies} routing policies, usage-policy={HasUsagePolicy}",
                plans.Count, clients.Count, pricing.Count, routingPolicies.Count, usagePolicy is not null);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable during cache warming — data loaded from Cosmos but cache is cold. " +
                "Reads will fall back to Cosmos until Redis recovers.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
