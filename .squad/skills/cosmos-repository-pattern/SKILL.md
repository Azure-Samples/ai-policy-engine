# Skill: Cosmos Repository Pattern with Redis Cache

## When to Use
Adding a new entity type that needs durable persistence with fast reads.

## Pattern
1. **Model**: Add `Id` and `PartitionKey` properties to the entity class
2. **Cosmos Repo**: Create a concrete class extending `CosmosRepositoryBase<T>`. Override `PrepareForCosmos` to set Id + PartitionKey
3. **DI Wiring**: Register concrete repo as singleton, then register `CachedRepository<T>` as `IRepository<T>` with Redis key functions
4. **Test Fixture**: Add `RedisBackedRepository<T>` registration in `ChargebackApiFactory.cs`

## Template

```csharp
// 1. Model
public sealed class MyEntity {
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = "my-entity";
    // ... fields
}

// 2. Repo
public sealed class CosmosMyEntityRepository : CosmosRepositoryBase<MyEntity> {
    public CosmosMyEntityRepository(ConfigurationContainerProvider p, ILogger<CosmosMyEntityRepository> l)
        : base(p, "my-entity", l) { }
    protected override void PrepareForCosmos(MyEntity e) {
        e.Id = e.SomeId;
        e.PartitionKey = "my-entity";
    }
}

// 3. Program.cs
builder.Services.AddSingleton<CosmosMyEntityRepository>();
builder.Services.AddSingleton<IRepository<MyEntity>>(sp =>
    new CachedRepository<MyEntity>(
        sp.GetRequiredService<CosmosMyEntityRepository>(),
        sp.GetRequiredService<IConnectionMultiplexer>(),
        id => $"my-entity:{id}",
        entity => entity.SomeId,
        sp.GetRequiredService<ILogger<CachedRepository<MyEntity>>>()));

// 4. Test fixture (ChargebackApiFactory.cs)
RemoveService<IRepository<MyEntity>>(services);
services.AddSingleton<IRepository<MyEntity>>(
    new RedisBackedRepository<MyEntity>(Redis, id => $"my-entity:{id}", e => e.SomeId, "my-entity:*"));
```

## Key Constraints
- Cosmos partition key value must match the `PartitionKey` property default and the string in `PrepareForCosmos`
- Redis key function in `CachedRepository` must match `RedisKeys.*` patterns
- `GetAllAsync` queries Cosmos (not Redis scan) for completeness
- `CacheWarmingService.cs` must be updated to warm the new entity type on startup
