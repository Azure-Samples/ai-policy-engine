using StackExchange.Redis;

namespace Chargeback.Api.Services;

/// <summary>
/// Extension methods for StackExchange.Redis that work correctly with
/// Redis Enterprise OSS Cluster mode, where keys are distributed across
/// multiple shards. <c>server.Keys()</c> on a single server only scans
/// that shard; these helpers scan ALL servers and merge results.
/// </summary>
public static class RedisExtensions
{
    /// <summary>
    /// Scans all servers in the Redis cluster for keys matching the given pattern.
    /// This is the cluster-safe replacement for <c>redis.GetServers().First().Keys(pattern)</c>.
    /// </summary>
    public static RedisKey[] KeysFromAllServers(this IConnectionMultiplexer redis, string pattern)
    {
        var allKeys = new HashSet<string>();
        foreach (var server in redis.GetServers())
        {
            if (server.IsConnected && !server.IsReplica)
            {
                foreach (var key in server.Keys(pattern: pattern))
                {
                    allKeys.Add(key.ToString());
                }
            }
        }
        return allKeys.Select(k => (RedisKey)k).ToArray();
    }
}
