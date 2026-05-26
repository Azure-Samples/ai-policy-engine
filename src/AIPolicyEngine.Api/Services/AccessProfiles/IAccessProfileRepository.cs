using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services.AccessProfiles;

public interface IAccessProfileRepository
{
    Task<AccessProfile?> GetAsync(string profileId, CancellationToken ct = default);
    Task<AccessProfile?> GetForScopeAsync(string clientAppId, string tenantId, string apiId, string? operationId, CancellationToken ct = default);
    Task<List<AccessProfile>> ListAsync(string? clientAppId = null, string? tenantId = null, string? apiId = null, CancellationToken ct = default);
    Task<AccessProfile> UpsertAsync(AccessProfile profile, CancellationToken ct = default);
    Task<bool> DeleteAsync(string profileId, CancellationToken ct = default);
}
