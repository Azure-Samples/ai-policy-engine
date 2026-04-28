using System.Text.Json;
using AIPolicyEngine.Api.Services;
using AIPolicyEngine.Tests.Repositories;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace AIPolicyEngine.Tests.Integration;

/// <summary>
/// B5.9 — Integration tests: Cosmos persistence resilience.
/// Validates that the Cosmos-as-source-of-truth architecture works under
/// cache failures, evictions, and full CRUD cycles through the repository pattern.
/// </summary>
public class CosmosPersistenceResilienceTests
{
    private readonly IRepository<TestEntity> _cosmosRepo;
    private readonly FakeRedis _fakeRedis;
    private readonly ILogger? _logger;
    private readonly CachedRepository<TestEntity> _sut;

    public CosmosPersistenceResilienceTests()
    {
        _cosmosRepo = Substitute.For<IRepository<TestEntity>>();
        _fakeRedis = new FakeRedis();
        _logger = Substitute.For<ILogger>();

        _sut = new CachedRepository<TestEntity>(
            _cosmosRepo,
            _fakeRedis.Multiplexer,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: _logger);
    }

    // ═══════════════════════════════════════════════════════════════
    // Write to Cosmos → clear Redis → read back from Cosmos
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task WritePlan_ClearRedis_ReadBack_GetsFromCosmosAndRepopulatesRedis()
    {
        // Arrange — write entity through cached repo (writes to Cosmos + Redis)
        var entity = new TestEntity { Id = "plan-gold", Name = "Gold Plan" };
        _cosmosRepo.UpsertAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);
        _cosmosRepo.GetAsync("plan-gold", Arg.Any<CancellationToken>()).Returns(entity);

        await _sut.UpsertAsync(entity);

        // Verify Redis was populated
        var cachedAfterWrite = await _fakeRedis.Database.StringGetAsync("test:plan-gold");
        Assert.True(cachedAfterWrite.HasValue, "Redis should have data after write");

        // Act — clear Redis cache (simulate eviction / restart)
        _fakeRedis.Clear();

        var cachedAfterClear = await _fakeRedis.Database.StringGetAsync("test:plan-gold");
        Assert.False(cachedAfterClear.HasValue, "Redis should be empty after clear");

        // Read back — should fall through to Cosmos and repopulate Redis
        var result = await _sut.GetAsync("plan-gold");

        // Assert — got the data from Cosmos
        Assert.NotNull(result);
        Assert.Equal("plan-gold", result.Id);
        Assert.Equal("Gold Plan", result.Name);
        await _cosmosRepo.Received(1).GetAsync("plan-gold", Arg.Any<CancellationToken>());

        // Assert — Redis was repopulated
        var repopulated = await _fakeRedis.Database.StringGetAsync("test:plan-gold");
        Assert.True(repopulated.HasValue, "Redis should be repopulated after Cosmos fallback read");
    }

    // ═══════════════════════════════════════════════════════════════
    // Redis unavailable (throws) → falls back to Cosmos
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task WritePricing_RedisUnavailable_ReadFallsBackToCosmos()
    {
        // Arrange — use a broken Redis that throws on read
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var cosmosRepo = Substitute.For<IRepository<TestEntity>>();
        var entity = new TestEntity { Id = "pricing-gpt4", Name = "GPT-4 Pricing" };
        cosmosRepo.GetAsync("pricing-gpt4", Arg.Any<CancellationToken>()).Returns(entity);

        var sut = new CachedRepository<TestEntity>(
            cosmosRepo, brokenRedis,
            redisKeyFromId: id => $"pricing:{id}",
            entityId: e => e.Id,
            logger: _logger);

        // Act — read with Redis completely down
        var result = await sut.GetAsync("pricing-gpt4");

        // Assert — seamless Cosmos fallback
        Assert.NotNull(result);
        Assert.Equal("pricing-gpt4", result.Id);
        Assert.Equal("GPT-4 Pricing", result.Name);
        await cosmosRepo.Received(1).GetAsync("pricing-gpt4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WritePricing_RedisThrowsOnWrite_CosmosDataSafe()
    {
        // Arrange — Redis throws on StringSetAsync (write path)
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));
        brokenDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var cosmosRepo = Substitute.For<IRepository<TestEntity>>();
        var entity = new TestEntity { Id = "pricing-mini", Name = "Mini Pricing" };
        cosmosRepo.UpsertAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = new CachedRepository<TestEntity>(
            cosmosRepo, brokenRedis,
            redisKeyFromId: id => $"pricing:{id}",
            entityId: e => e.Id,
            logger: _logger);

        // Act — write should succeed (Cosmos is source of truth)
        var result = await sut.UpsertAsync(entity);

        // Assert — Cosmos write happened, data returned despite Redis failure
        Assert.NotNull(result);
        Assert.Equal("Mini Pricing", result.Name);
        await cosmosRepo.Received(1).UpsertAsync(entity, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Client assignment: kill Redis key → transparent cache rebuild
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task WriteClientAssignment_KillRedisKey_ReadBack_TransparentCacheRebuild()
    {
        // Arrange — write entity, then surgically delete just that key from Redis
        var entity = new TestEntity { Id = "client-abc:tenant-1", Name = "Acme Corp" };
        _cosmosRepo.UpsertAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);
        _cosmosRepo.GetAsync("client-abc:tenant-1", Arg.Any<CancellationToken>()).Returns(entity);

        await _sut.UpsertAsync(entity);

        // Kill just the client key (simulate targeted eviction)
        await _fakeRedis.Database.KeyDeleteAsync("test:client-abc:tenant-1");

        // Act — read back
        var result = await _sut.GetAsync("client-abc:tenant-1");

        // Assert — got data from Cosmos, cache rebuilt
        Assert.NotNull(result);
        Assert.Equal("Acme Corp", result.Name);
        await _cosmosRepo.Received(1).GetAsync("client-abc:tenant-1", Arg.Any<CancellationToken>());

        // Verify Redis was repopulated
        var rebuilt = await _fakeRedis.Database.StringGetAsync("test:client-abc:tenant-1");
        Assert.True(rebuilt.HasValue, "Redis cache should be rebuilt transparently after key eviction");
    }

    // ═══════════════════════════════════════════════════════════════
    // Full CRUD cycle through repository
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullCrudCycle_Create_Read_Update_Delete_CosmosHasDataAtEachStep()
    {
        // ── CREATE ──
        var entity = new TestEntity { Id = "crud-1", Name = "Original" };
        _cosmosRepo.UpsertAsync(Arg.Is<TestEntity>(e => e.Id == "crud-1"), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TestEntity>());
        _cosmosRepo.GetAsync("crud-1", Arg.Any<CancellationToken>())
            .Returns(entity);

        var created = await _sut.UpsertAsync(entity);
        Assert.Equal("Original", created.Name);
        await _cosmosRepo.Received(1).UpsertAsync(
            Arg.Is<TestEntity>(e => e.Id == "crud-1"),
            Arg.Any<CancellationToken>());

        // ── READ ── (should hit Redis cache)
        _cosmosRepo.ClearReceivedCalls();
        var read = await _sut.GetAsync("crud-1");
        Assert.NotNull(read);
        Assert.Equal("Original", read.Name);
        // Cache hit — Cosmos should NOT be called
        await _cosmosRepo.DidNotReceive().GetAsync("crud-1", Arg.Any<CancellationToken>());

        // ── UPDATE ──
        var updated = new TestEntity { Id = "crud-1", Name = "Updated" };
        _cosmosRepo.UpsertAsync(Arg.Is<TestEntity>(e => e.Name == "Updated"), Arg.Any<CancellationToken>())
            .Returns(updated);
        _cosmosRepo.GetAsync("crud-1", Arg.Any<CancellationToken>()).Returns(updated);

        var updateResult = await _sut.UpsertAsync(updated);
        Assert.Equal("Updated", updateResult.Name);
        await _cosmosRepo.Received(1).UpsertAsync(
            Arg.Is<TestEntity>(e => e.Name == "Updated"),
            Arg.Any<CancellationToken>());

        // Verify Redis has updated data
        var cachedJson = await _fakeRedis.Database.StringGetAsync("test:crud-1");
        Assert.True(cachedJson.HasValue);
        var cachedEntity = JsonSerializer.Deserialize<TestEntity>((string)cachedJson!, JsonConfig.Default);
        Assert.Equal("Updated", cachedEntity!.Name);

        // ── DELETE ──
        _cosmosRepo.DeleteAsync("crud-1", Arg.Any<CancellationToken>()).Returns(true);
        var deleted = await _sut.DeleteAsync("crud-1");
        Assert.True(deleted);
        await _cosmosRepo.Received(1).DeleteAsync("crud-1", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent reads during cache miss
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentReads_DuringCacheMiss_AllSucceed_NoErrors()
    {
        // Arrange — Redis empty, Cosmos has data (with slight delay to simulate I/O)
        var entity = new TestEntity { Id = "concurrent-1", Name = "Shared Data" };
        _cosmosRepo.GetAsync("concurrent-1", Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(10).ContinueWith(_ => (TestEntity?)entity));

        // Act — fire 10 parallel reads
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _sut.GetAsync("concurrent-1"))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert — all reads succeed with correct data, no exceptions
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("concurrent-1", r!.Id);
            Assert.Equal("Shared Data", r.Name);
        });
    }

    [Fact]
    public async Task ConcurrentReads_MixedCacheHitAndMiss_AllSucceed()
    {
        // Arrange — seed one key, leave another empty
        var entity1 = new TestEntity { Id = "mix-1", Name = "Cached" };
        var entity2 = new TestEntity { Id = "mix-2", Name = "Uncached" };

        // Seed entity1 in Redis (cache hit path)
        _fakeRedis.SeedString("test:mix-1", JsonSerializer.Serialize(entity1, JsonConfig.Default));

        // entity2 only in Cosmos (cache miss path)
        _cosmosRepo.GetAsync("mix-2", Arg.Any<CancellationToken>()).Returns(entity2);

        // Act — concurrent reads for both
        var hitTasks = Enumerable.Range(0, 5).Select(_ => _sut.GetAsync("mix-1"));
        var missTasks = Enumerable.Range(0, 5).Select(_ => _sut.GetAsync("mix-2"));
        var allTasks = hitTasks.Concat(missTasks).ToList();

        var results = await Task.WhenAll(allTasks);

        // Assert — all 10 reads succeeded
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(5, results.Count(r => r!.Name == "Cached"));
        Assert.Equal(5, results.Count(r => r!.Name == "Uncached"));
    }

    // ═══════════════════════════════════════════════════════════════
    // GetAllAsync resilience
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAllAsync_ClearRedis_FallsBackToCosmos()
    {
        // Arrange — Cosmos has entities
        var entities = new List<TestEntity>
        {
            new() { Id = "a1", Name = "Alpha" },
            new() { Id = "a2", Name = "Beta" }
        };
        _cosmosRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(entities);

        // First call populates Redis cache
        var first = await _sut.GetAllAsync();
        Assert.Equal(2, first.Count);

        // Clear Redis (simulate restart)
        _fakeRedis.Clear();

        // Act — second call should fall back to Cosmos
        var second = await _sut.GetAllAsync();

        // Assert — both calls return full data
        Assert.Equal(2, second.Count);
        await _cosmosRepo.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAsync_RedisThrows_FallsBackToCosmos()
    {
        // Arrange — Redis totally broken
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var cosmosRepo = Substitute.For<IRepository<TestEntity>>();
        var entities = new List<TestEntity>
        {
            new() { Id = "x1", Name = "X-Ray" },
            new() { Id = "x2", Name = "Yankee" }
        };
        cosmosRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(entities);

        var sut = new CachedRepository<TestEntity>(
            cosmosRepo, brokenRedis,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: _logger);

        // Act
        var result = await sut.GetAllAsync();

        // Assert — fell back to Cosmos
        Assert.Equal(2, result.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // Delete resilience: Cosmos source of truth
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_RedisThrows_CosmosDeleteStillSucceeds()
    {
        // Arrange — Redis broken on KeyDeleteAsync
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var cosmosRepo = Substitute.For<IRepository<TestEntity>>();
        cosmosRepo.DeleteAsync("del-1", Arg.Any<CancellationToken>()).Returns(true);

        var sut = new CachedRepository<TestEntity>(
            cosmosRepo, brokenRedis,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: _logger);

        // Act — should not throw
        var result = await sut.DeleteAsync("del-1");

        // Assert — Cosmos delete succeeded
        Assert.True(result);
        await cosmosRepo.Received(1).DeleteAsync("del-1", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // End-to-end: write → evict → read → verify coherence
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EndToEnd_MultipleEntities_EvictAll_ReadAll_CosmosRebuildsFully()
    {
        // Arrange — write 5 entities
        var entities = Enumerable.Range(1, 5)
            .Select(i => new TestEntity { Id = $"e2e-{i}", Name = $"Entity {i}" })
            .ToList();

        foreach (var e in entities)
        {
            _cosmosRepo.UpsertAsync(
                Arg.Is<TestEntity>(x => x.Id == e.Id),
                Arg.Any<CancellationToken>()).Returns(e);
            _cosmosRepo.GetAsync(e.Id, Arg.Any<CancellationToken>()).Returns(e);
            await _sut.UpsertAsync(e);
        }

        // Evict all from Redis
        _fakeRedis.Clear();

        // Act — read all back individually
        var results = new List<TestEntity?>();
        foreach (var e in entities)
        {
            results.Add(await _sut.GetAsync(e.Id));
        }

        // Assert — all came back from Cosmos and are correct
        Assert.All(results, r => Assert.NotNull(r));
        for (int i = 0; i < entities.Count; i++)
        {
            Assert.Equal(entities[i].Id, results[i]!.Id);
            Assert.Equal(entities[i].Name, results[i]!.Name);
        }

        // Verify all are now back in Redis
        foreach (var e in entities)
        {
            var cached = await _fakeRedis.Database.StringGetAsync($"test:{e.Id}");
            Assert.True(cached.HasValue, $"Redis should have {e.Id} after Cosmos fallback");
        }
    }
}
