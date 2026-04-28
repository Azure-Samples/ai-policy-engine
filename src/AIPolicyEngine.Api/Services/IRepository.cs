namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Generic repository contract. Cosmos is the store, Redis is the cache.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetAsync(string id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task<T> UpsertAsync(T entity, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
