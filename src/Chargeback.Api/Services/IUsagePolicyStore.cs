using Chargeback.Api.Models;
using StackExchange.Redis;

namespace Chargeback.Api.Services;

public interface IUsagePolicyStore
{
    Task<UsagePolicySettings> GetAsync(IDatabase db);
    Task<UsagePolicySettings> UpdateAsync(IDatabase db, UsagePolicyUpdateRequest request);
}
