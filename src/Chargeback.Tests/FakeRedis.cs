using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NSubstitute;
using StackExchange.Redis;

namespace Chargeback.Tests;

/// <summary>
/// In-memory Redis substitute backed by ConcurrentDictionary.
/// Uses NSubstitute to implement the StackExchange.Redis interfaces
/// with only the methods the application actually calls.
/// </summary>
public sealed class FakeRedis
{
    private readonly ConcurrentDictionary<string, RedisValue> _strings = new();
    private readonly ConcurrentDictionary<string, List<RedisValue>> _lists = new();

    public IConnectionMultiplexer Multiplexer { get; }
    public IDatabase Database { get; }
    public IServer Server { get; }

    public FakeRedis()
    {
        Database = Substitute.For<IDatabase>();
        Server = Substitute.For<IServer>();
        Multiplexer = Substitute.For<IConnectionMultiplexer>();

        Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Database);
        Multiplexer.GetServers().Returns(new[] { Server });

        SetupStringOps();
        SetupKeyOps();
        SetupListOps();
        SetupServerOps();
    }

    public void Clear()
    {
        _strings.Clear();
        _lists.Clear();
    }

    // Seed a string key directly (useful for test setup)
    public void SeedString(string key, string value) => _strings[key] = value;

    // Seed a list key directly
    public void SeedList(string key, params string[] values)
    {
        _lists[key] = new List<RedisValue>(values.Select(v => (RedisValue)v));
    }

    private void SetupStringOps()
    {
        Database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return _strings.TryGetValue(key, out var val) ? val : RedisValue.Null;
            });

        // All StringSetAsync overloads delegate to the same store logic.
        Func<NSubstitute.Core.CallInfo, bool> stringSetHandler = ci =>
        {
            var key = ((RedisKey)ci[0]).ToString();
            _strings[key] = (RedisValue)ci[1];
            return true;
        };

        // 4-arg: (RedisKey, RedisValue, TimeSpan?, When) — resolves for db.StringSetAsync(key, value) and db.StringSetAsync(key, value, ttl)
        Database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(stringSetHandler);

        // 5-arg: (RedisKey, RedisValue, TimeSpan?, When, CommandFlags)
        Database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(stringSetHandler);

        // 6-arg: (RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags)
        Database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(stringSetHandler);

        // Newest overload in newer StackExchange.Redis: (RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)
        Database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(stringSetHandler);

        Database.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                var inc = (long)ci[1];
                var newVal = _strings.AddOrUpdate(
                    key,
                    _ => inc,
                    (_, existing) => (long)existing + inc);
                return (long)newVal;
            });
    }

    private void SetupKeyOps()
    {
        Database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return _strings.TryRemove(key, out _) || _lists.TryRemove(key, out _);
            });

        Database.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return _strings.ContainsKey(key) || _lists.ContainsKey(key);
            });

        Database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>())
            .Returns(true);

        Database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>())
            .Returns(true);

        Database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                var value = (RedisValue)ci[1];
                return _strings.TryAdd(key, value);
            });

        Database.LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                var value = (RedisValue)ci[1];
                if (_strings.TryGetValue(key, out var existing) && existing == value)
                    return _strings.TryRemove(key, out _);

                return false;
            });
    }

    private void SetupListOps()
    {
        Database.ListLeftPushAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                var value = (RedisValue)ci[1];
                var list = _lists.GetOrAdd(key, _ => new List<RedisValue>());
                lock (list) { list.Insert(0, value); }
                return (long)list.Count;
            });

        Database.ListTrimAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(Task.CompletedTask);

        Database.WhenForAnyArgs(x => x.ListTrimAsync(default, default, default, default))
            .Do(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                var stop = (long)ci[2];
                if (_lists.TryGetValue(key, out var list))
                {
                    lock (list)
                    {
                        if (stop + 1 < list.Count)
                            list.RemoveRange((int)(stop + 1), list.Count - (int)(stop + 1));
                    }
                }
            });

        Database.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                if (!_lists.TryGetValue(key, out var list))
                    return Array.Empty<RedisValue>();
                lock (list) { return list.ToArray(); }
            });
    }

    private void SetupServerOps()
    {
        Server.IsConnected.Returns(true);
        Server.IsReplica.Returns(false);

        Server.Keys(
                Arg.Any<int>(), Arg.Any<RedisValue>(), Arg.Any<int>(),
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var pattern = ((RedisValue)ci[1]).ToString();
                var allKeys = _strings.Keys.Concat(_lists.Keys).Distinct();
                return allKeys
                    .Where(k => MatchesGlob(k, pattern))
                    .Select(k => (RedisKey)k);
            });
    }

    private static bool MatchesGlob(string key, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(key, regex);
    }
}
