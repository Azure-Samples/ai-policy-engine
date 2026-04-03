using System.Text.Json;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

/// <summary>
/// Write-through cache layer. Wraps any IRepository&lt;T&gt; with Redis caching.
/// Write path: Cosmos first (source of truth), then update Redis cache.
/// Read path: Redis first (fast), fall back to Cosmos on cache miss.
/// </summary>
public sealed class CachedRepository<T> : IRepository<T> where T : class
{
    private readonly IRepository<T> _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly Func<string, string> _redisKeyFromId;
    private readonly Func<T, string> _entityId;
    private readonly ILogger _logger;

    public CachedRepository(
        IRepository<T> inner,
        IConnectionMultiplexer redis,
        Func<string, string> redisKeyFromId,
        Func<T, string> entityId,
        ILogger logger)
    {
        _inner = inner;
        _redis = redis;
        _redisKeyFromId = redisKeyFromId;
        _entityId = entityId;
        _logger = logger;
    }

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        // Try Redis first
        try
        {
            var db = _redis.GetDatabase();
            var cached = await db.StringGetAsync(_redisKeyFromId(id));
            if (cached.HasValue)
            {
                var deserialized = JsonSerializer.Deserialize<T>((string)cached!, JsonConfig.Default);
                if (deserialized is not null) return deserialized;
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis read failed for {EntityType}/{Id}, falling back to Cosmos", typeof(T).Name, id);
        }

        // Cache miss — read from Cosmos, populate Redis
        var entity = await _inner.GetAsync(id, ct);
        if (entity is not null)
        {
            await TryCacheEntity(entity);
        }
        return entity;
    }

    public async Task<List<T>> GetAllAsync(CancellationToken ct = default)
    {
        // GetAll always goes to Cosmos (source of truth for complete listings)
        var entities = await _inner.GetAllAsync(ct);

        // Refresh Redis cache for returned entities (parallel for throughput)
        var cacheTasks = entities.Select(e => TryCacheEntity(e));
        await Task.WhenAll(cacheTasks);

        return entities;
    }

    public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        // Write to Cosmos FIRST (source of truth)
        var persisted = await _inner.UpsertAsync(entity, ct);

        // Then update Redis cache
        await TryCacheEntity(persisted);

        return persisted;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        // Delete from Cosmos first
        var deleted = await _inner.DeleteAsync(id, ct);

        if (deleted)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.KeyDeleteAsync(_redisKeyFromId(id));
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Failed to remove {EntityType}/{Id} from Redis cache", typeof(T).Name, id);
            }
        }

        return deleted;
    }

    private async Task TryCacheEntity(T entity)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = _redisKeyFromId(_entityId(entity));
            var json = JsonSerializer.Serialize(entity, JsonConfig.Default);
            await db.StringSetAsync(key, json);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to cache {EntityType} in Redis", typeof(T).Name);
        }
    }
}
