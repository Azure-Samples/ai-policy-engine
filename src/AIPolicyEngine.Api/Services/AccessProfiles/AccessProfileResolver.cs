using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Api.Services.AccessProfiles;

public sealed class AccessProfileResolver : IAccessProfileResolver
{
    private readonly IAccessProfileRepository _accessProfileRepository;
    private readonly IRepository<ClientPlanAssignment> _clientRepository;

    public AccessProfileResolver(
        IAccessProfileRepository accessProfileRepository,
        IRepository<ClientPlanAssignment> clientRepository)
    {
        _accessProfileRepository = accessProfileRepository;
        _clientRepository = clientRepository;
    }

    public async Task<ResolvedAccessProfile?> ResolveAsync(
        string clientAppId,
        string tenantId,
        string apiId,
        string? operationId,
        CancellationToken ct = default)
    {
        var normalizedOperationId = string.IsNullOrWhiteSpace(operationId) ? null : operationId.Trim();

        if (normalizedOperationId is not null)
        {
            var operationProfile = await _accessProfileRepository.GetForScopeAsync(clientAppId, tenantId, apiId, normalizedOperationId, ct);
            if (operationProfile is { Enabled: true })
                return ToResolved(operationProfile);
        }

        var apiProfile = await _accessProfileRepository.GetForScopeAsync(clientAppId, tenantId, apiId, null, ct);
        if (apiProfile is { Enabled: true })
            return ToResolved(apiProfile);

        var globalProfile = await _accessProfileRepository.GetForScopeAsync(clientAppId, tenantId, AccessProfile.GlobalApiId, null, ct);
        if (globalProfile is { Enabled: true })
            return ToResolved(globalProfile);

        var clientAssignment = await _clientRepository.GetAsync($"{clientAppId}:{tenantId}", ct);
        if (clientAssignment is null)
            return null;

        return new ResolvedAccessProfile
        {
            PlanId = clientAssignment.PlanId,
            RoutingPolicyId = clientAssignment.ModelRoutingPolicyOverride,
            AllowedDeployments = clientAssignment.AllowedDeployments?.ToList() ?? [],
            AccessProfileId = null
        };
    }

    private static ResolvedAccessProfile ToResolved(AccessProfile profile) => new()
    {
        PlanId = profile.PlanId,
        RoutingPolicyId = profile.RoutingPolicyId,
        AllowedDeployments = profile.AllowedDeployments?.ToList() ?? [],
        AccessProfileId = profile.Id
    };
}
