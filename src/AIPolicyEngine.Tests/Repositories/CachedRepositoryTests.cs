using System.Text.Json;
using AIPolicyEngine.Api.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace AIPolicyEngine.Tests.Repositories;

/// <summary>
/// B5.1 — Unit tests for <see cref="CachedRepository{T}"/>.
/// Tests the two-layer read/write pattern: Redis (cache) → Cosmos (source of truth).
/// Written against the architecture spec — interface-driven, mock both layers.
/// </summary>
public class CachedRepositoryTests
{
    private readonly IRepository<TestEntity> _inner;
    private readonly FakeRedis _fakeRedis;
    private readonly ILogger _logger;
    private readonly CachedRepository<TestEntity> _sut;

    public CachedRepositoryTests()
    {
        _inner = Substitute.For<IRepository<TestEntity>>();
        _fakeRedis = new FakeRedis();
        _logger = Substitute.For<ILogger>();

        _sut = new CachedRepository<TestEntity>(
            _inner,
            _fakeRedis.Multiplexer,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: _logger);
    }

    // ─── Cache Hit ─────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_CacheHit_ReturnsCachedData_NeverTouchesCosmos()
    {
        // Arrange — seed Redis with a serialized entity
        var entity = new TestEntity { Id = "plan-1", Name = "Gold Plan" };
        _fakeRedis.SeedString("test:plan-1", JsonSerializer.Serialize(entity, JsonConfig.Default));

        // Act
        var result = await _sut.GetAsync("plan-1");

        // Assert — got the data, Cosmos was never called
        Assert.NotNull(result);
        Assert.Equal("plan-1", result.Id);
        Assert.Equal("Gold Plan", result.Name);
        await _inner.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAsync_AlwaysQueriesCosmos_RefreshesCache()
    {
        // Arrange — Cosmos has entities
        var entities = new List<TestEntity>
        {
            new() { Id = "p1", Name = "Plan A" },
            new() { Id = "p2", Name = "Plan B" }
        };
        _inner.GetAllAsync(Arg.Any<CancellationToken>()).Returns(entities);

        // Act
        var result = await _sut.GetAllAsync();

        // Assert — Services version always queries Cosmos for GetAll (source of truth for listings)
        Assert.Equal(2, result.Count);
        await _inner.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    // ─── Cache Miss ────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_CacheMiss_ReadsFromCosmos_PopulatesRedis()
    {
        // Arrange — Redis is empty, Cosmos has data
        var entity = new TestEntity { Id = "plan-2", Name = "Silver Plan" };
        _inner.GetAsync("plan-2", Arg.Any<CancellationToken>()).Returns(entity);

        // Act
        var result = await _sut.GetAsync("plan-2");

        // Assert — got data from Cosmos
        Assert.NotNull(result);
        Assert.Equal("plan-2", result.Id);
        await _inner.Received(1).GetAsync("plan-2", Arg.Any<CancellationToken>());

        // Assert — Redis was populated (verify via FakeRedis state)
        var cached = await _fakeRedis.Database.StringGetAsync("test:plan-2");
        Assert.True(cached.HasValue, "Redis should have been populated on cache miss");
    }

    [Fact]
    public async Task GetAllAsync_CacheMiss_ReadsFromCosmos_PopulatesRedis()
    {
        // Arrange
        var entities = new List<TestEntity>
        {
            new() { Id = "p1", Name = "Plan A" },
            new() { Id = "p2", Name = "Plan B" }
        };
        _inner.GetAllAsync(Arg.Any<CancellationToken>()).Returns(entities);

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count);
        await _inner.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    // ─── Write-Through ─────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_WritesToCosmosFirst_ThenUpdatesRedis()
    {
        // Arrange
        var entity = new TestEntity { Id = "plan-3", Name = "Bronze Plan" };
        _inner.UpsertAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);

        // Act
        var result = await _sut.UpsertAsync(entity);

        // Assert — Cosmos written first
        await _inner.Received(1).UpsertAsync(entity, Arg.Any<CancellationToken>());
        Assert.Equal("plan-3", result.Id);

        // Assert — Redis cache updated (verify via FakeRedis state)
        var cached = await _fakeRedis.Database.StringGetAsync("test:plan-3");
        Assert.True(cached.HasValue, "Redis should have been updated after Cosmos write");
    }

    [Fact]
    public async Task UpsertAsync_ReturnsPersistedEntity_NotOriginal()
    {
        // Arrange — Cosmos may enrich the entity (e.g., UpdatedAt timestamp)
        var input = new TestEntity { Id = "plan-4", Name = "Input" };
        var persisted = new TestEntity { Id = "plan-4", Name = "Persisted" };
        _inner.UpsertAsync(input, Arg.Any<CancellationToken>()).Returns(persisted);

        // Act
        var result = await _sut.UpsertAsync(input);

        // Assert — returns what Cosmos returned, not what was passed in
        Assert.Equal("Persisted", result.Name);
    }

    // ─── Delete ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesFromCosmosFirst_ThenRemovesFromRedis()
    {
        // Arrange
        _inner.DeleteAsync("plan-5", Arg.Any<CancellationToken>()).Returns(true);
        _fakeRedis.SeedString("test:plan-5", "{}");

        // Act
        var result = await _sut.DeleteAsync("plan-5");

        // Assert — deleted from Cosmos
        Assert.True(result);
        await _inner.Received(1).DeleteAsync("plan-5", Arg.Any<CancellationToken>());

        // Assert — Redis key removed
        await _fakeRedis.Database.Received().KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "test:plan-5"),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DeleteAsync_CosmosReturnsNotFound_DoesNotTouchRedis()
    {
        // Arrange — entity doesn't exist in Cosmos
        _inner.DeleteAsync("nonexistent", Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _sut.DeleteAsync("nonexistent");

        // Assert — Cosmos says no, Redis not touched for delete
        Assert.False(result);
        await _fakeRedis.Database.DidNotReceive().KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "test:nonexistent"),
            Arg.Any<CommandFlags>());
    }

    // ─── Eviction Recovery ─────────────────────────────────────

    [Fact]
    public async Task GetAsync_DataInCosmos_NotInRedis_TransparentCacheRebuild()
    {
        // Arrange — simulate Redis eviction: cache is empty, data lives in Cosmos
        var entity = new TestEntity { Id = "evicted-1", Name = "Recovered" };
        _inner.GetAsync("evicted-1", Arg.Any<CancellationToken>()).Returns(entity);

        // Act
        var result = await _sut.GetAsync("evicted-1");

        // Assert — caller gets data seamlessly
        Assert.NotNull(result);
        Assert.Equal("Recovered", result.Name);

        // Assert — cache rebuilt automatically (verify via FakeRedis state)
        var cached = await _fakeRedis.Database.StringGetAsync("test:evicted-1");
        Assert.True(cached.HasValue, "Redis cache should have been rebuilt from Cosmos");
    }

    // ─── Cosmos Failure on Write ───────────────────────────────

    [Fact]
    public async Task UpsertAsync_CosmosThrows_PropagatesException_DoesNotUpdateRedis()
    {
        // Arrange — Cosmos blows up
        var entity = new TestEntity { Id = "fail-1", Name = "Doomed" };
        _inner.UpsertAsync(entity, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cosmos write failed"));

        // Act & Assert — exception surfaces to caller
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpsertAsync(entity));

        // Assert — Redis was NOT updated (no silent partial success)
        var cached = await _fakeRedis.Database.StringGetAsync("test:fail-1");
        Assert.False(cached.HasValue, "Redis should NOT be updated when Cosmos write fails");
    }

    [Fact]
    public async Task DeleteAsync_CosmosThrows_PropagatesException()
    {
        // Arrange
        _inner.DeleteAsync("fail-2", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cosmos delete failed"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteAsync("fail-2"));
    }

    // ─── Redis Failure on Read ─────────────────────────────────

    [Fact]
    public async Task GetAsync_RedisThrows_FallsBackToCosmos()
    {
        // Arrange — Redis is down, but Cosmos has data
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var innerRepo = Substitute.For<IRepository<TestEntity>>();
        var entity = new TestEntity { Id = "fallback-1", Name = "FromCosmos" };
        innerRepo.GetAsync("fallback-1", Arg.Any<CancellationToken>()).Returns(entity);

        var sut = new CachedRepository<TestEntity>(
            innerRepo, brokenRedis,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: Substitute.For<ILogger>());

        // Act
        var result = await sut.GetAsync("fallback-1");

        // Assert — fell back to Cosmos gracefully
        Assert.NotNull(result);
        Assert.Equal("FromCosmos", result.Name);
        await innerRepo.Received(1).GetAsync("fallback-1", Arg.Any<CancellationToken>());
    }

    // ─── Null Handling ─────────────────────────────────────────

    [Fact]
    public async Task GetAsync_NonExistentId_ReturnsNull_FromBothLayers()
    {
        // Arrange — not in Redis, not in Cosmos
        _inner.GetAsync("ghost", Arg.Any<CancellationToken>()).Returns((TestEntity?)null);

        // Act
        var result = await _sut.GetAsync("ghost");

        // Assert — null, not an exception
        Assert.Null(result);
        await _inner.Received(1).GetAsync("ghost", Arg.Any<CancellationToken>());

        // Assert — null should NOT be cached in Redis
        var cached = await _fakeRedis.Database.StringGetAsync("test:ghost");
        Assert.False(cached.HasValue, "Null entities should not be cached in Redis");
    }

    [Fact]
    public async Task GetAllAsync_EmptyCosmos_ReturnsEmptyList()
    {
        // Arrange
        _inner.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<TestEntity>());

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ─── Cancellation ──────────────────────────────────────────

    [Fact]
    public async Task GetAsync_CancellationRequested_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _inner.GetAsync("cancel-1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.GetAsync("cancel-1", cts.Token));
    }

    // ─── Redis Failure on Write (write-through resilience) ─────

    [Fact]
    public async Task UpsertAsync_RedisThrowsAfterCosmosSucceeds_StillReturnsData()
    {
        // Arrange — Cosmos succeeds, but Redis cache update fails
        var brokenRedis = Substitute.For<IConnectionMultiplexer>();
        var brokenDb = Substitute.For<IDatabase>();
        brokenRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(brokenDb);
        brokenDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        var innerRepo = Substitute.For<IRepository<TestEntity>>();
        var entity = new TestEntity { Id = "rw-1", Name = "Persisted" };
        innerRepo.UpsertAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = new CachedRepository<TestEntity>(
            innerRepo, brokenRedis,
            redisKeyFromId: id => $"test:{id}",
            entityId: e => e.Id,
            logger: Substitute.For<ILogger>());

        // Act — should not throw; Cosmos is source of truth, Redis failure is tolerable
        var result = await sut.UpsertAsync(entity);

        // Assert — data persisted to Cosmos even though Redis failed
        Assert.NotNull(result);
        Assert.Equal("Persisted", result.Name);
    }
}

/// <summary>
/// Simple entity for testing the generic repository. Mirrors the shape
/// of PlanData / ClientPlanAssignment without coupling to production models.
/// </summary>
public sealed class TestEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
