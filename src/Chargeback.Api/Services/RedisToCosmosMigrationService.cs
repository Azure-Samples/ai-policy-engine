using System.Text.Json;
using Chargeback.Api.Models;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

/// <summary>
/// One-time migration: scans Redis for existing plan/client/pricing/usage-policy keys
/// and writes any missing documents to Cosmos. Idempotent — safe to run every startup.
/// </summary>
public sealed class RedisToCosmosMigrationService : IHostedService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CosmosPlanRepository _planRepo;
    private readonly CosmosClientRepository _clientRepo;
    private readonly CosmosPricingRepository _pricingRepo;
    private readonly CosmosUsagePolicyRepository _usagePolicyRepo;
    private readonly ILogger<RedisToCosmosMigrationService> _logger;

    public RedisToCosmosMigrationService(
        IConnectionMultiplexer redis,
        CosmosPlanRepository planRepo,
        CosmosClientRepository clientRepo,
        CosmosPricingRepository pricingRepo,
        CosmosUsagePolicyRepository usagePolicyRepo,
        ILogger<RedisToCosmosMigrationService> logger)
    {
        _redis = redis;
        _planRepo = planRepo;
        _clientRepo = clientRepo;
        _pricingRepo = pricingRepo;
        _usagePolicyRepo = usagePolicyRepo;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Redis → Cosmos migration starting");

        try
        {
            var db = _redis.GetDatabase();

            var plans = await MigrateEntities(db, RedisKeys.PlanPrefix, _planRepo, p => p.Id, "plan", ct);
            var clients = await MigrateClients(db, ct);
            var pricing = await MigrateEntities(db, RedisKeys.PricingPrefix, _pricingRepo, p => p.ModelId, "pricing", ct);
            var usagePolicy = await MigrateUsagePolicy(db, ct);

            _logger.LogInformation(
                "Redis → Cosmos migration complete: {Plans} plans, {Clients} clients, {Pricing} pricing, {UsagePolicy} usage-policy",
                plans, clients, pricing, usagePolicy);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during migration — skipping (will retry next startup)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Migration encountered an error — skipping (will retry next startup)");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<int> MigrateEntities<T>(
        IDatabase db,
        string keyPattern,
        IRepository<T> cosmosRepo,
        Func<T, string> idSelector,
        string entityName,
        CancellationToken ct) where T : class
    {
        var keys = _redis.KeysFromAllServers(keyPattern);
        var migrated = 0;

        foreach (var key in keys)
        {
            try
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;

                var entity = JsonSerializer.Deserialize<T>((string)value!, JsonConfig.Default);
                if (entity is null) continue;

                var id = idSelector(entity);
                var existing = await cosmosRepo.GetAsync(id, ct);
                if (existing is null)
                {
                    await cosmosRepo.UpsertAsync(entity, ct);
                    migrated++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate {EntityName} key {Key}", entityName, key);
            }
        }

        return migrated;
    }

    private async Task<int> MigrateClients(IDatabase db, CancellationToken ct)
    {
        var keys = _redis.KeysFromAllServers(RedisKeys.ClientPrefix);
        var migrated = 0;

        foreach (var key in keys)
        {
            try
            {
                var value = await db.StringGetAsync(key);
                if (!value.HasValue) continue;

                var client = JsonSerializer.Deserialize<ClientPlanAssignment>((string)value!, JsonConfig.Default);
                if (client is null) continue;

                // Skip stale keys from pre-migration format
                if (string.IsNullOrWhiteSpace(client.TenantId)) continue;

                var id = $"{client.ClientAppId}:{client.TenantId}";
                var existing = await _clientRepo.GetAsync(id, ct);
                if (existing is null)
                {
                    await _clientRepo.UpsertAsync(client, ct);
                    migrated++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate client key {Key}", key);
            }
        }

        return migrated;
    }

    private async Task<int> MigrateUsagePolicy(IDatabase db, CancellationToken ct)
    {
        try
        {
            var value = await db.StringGetAsync(RedisKeys.UsagePolicySettings);
            if (!value.HasValue) return 0;

            var settings = JsonSerializer.Deserialize<UsagePolicySettings>((string)value!, JsonConfig.Default);
            if (settings is null) return 0;

            var existing = await _usagePolicyRepo.GetAsync("usage-policy", ct);
            if (existing is null)
            {
                await _usagePolicyRepo.UpsertAsync(settings, ct);
                return 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate usage policy");
        }

        return 0;
    }
}
