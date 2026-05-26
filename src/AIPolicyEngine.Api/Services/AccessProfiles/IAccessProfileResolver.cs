using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services.AccessProfiles;

public interface IAccessProfileResolver
{
    Task<ResolvedAccessProfile?> ResolveAsync(string clientAppId, string tenantId, string apiId, string? operationId, CancellationToken ct = default);
}
