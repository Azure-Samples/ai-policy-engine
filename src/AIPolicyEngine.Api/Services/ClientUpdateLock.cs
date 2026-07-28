using StackExchange.Redis;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Distributed lock guarding the read-modify-write cycle on a <c>ClientPlanAssignment</c>.
/// Any code path that mutates assignment counters (log ingest, REST log ingest, usage reset)
/// must hold this lock so concurrent writers don't clobber each other's updates.
/// </summary>
public static class ClientUpdateLock
{
    // TTL must cover the full read-compute-write cycle including Cosmos latency.
    // If the lock expires mid-operation, concurrent requests can read stale data.
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private const int MaxAttempts = 40;

    public static async Task<bool> TryAcquireAsync(
        IDatabase db,
        string clientAppId,
        string tenantId,
        RedisValue lockToken,
        ILogger logger)
    {
        var lockKey = RedisKeys.ClientUpdateLock(clientAppId, tenantId);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (await db.LockTakeAsync(lockKey, lockToken, Ttl))
                return true;

            await Task.Delay(RetryDelay);
        }

        logger.LogWarning("Failed to acquire client usage lock for {ClientAppId}/{TenantId}", clientAppId, tenantId);
        return false;
    }

    public static async Task ReleaseAsync(
        IDatabase db,
        string clientAppId,
        string tenantId,
        RedisValue lockToken,
        ILogger logger)
    {
        try
        {
            var lockKey = RedisKeys.ClientUpdateLock(clientAppId, tenantId);
            await db.LockReleaseAsync(lockKey, lockToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Failed to release client usage lock for {ClientAppId}/{TenantId}", clientAppId, tenantId);
        }
    }
}
