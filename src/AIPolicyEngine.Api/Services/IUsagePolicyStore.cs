using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

public interface IUsagePolicyStore
{
    Task<UsagePolicySettings> GetAsync(CancellationToken ct = default);
    Task<UsagePolicySettings> UpdateAsync(UsagePolicyUpdateRequest request, CancellationToken ct = default);
}
