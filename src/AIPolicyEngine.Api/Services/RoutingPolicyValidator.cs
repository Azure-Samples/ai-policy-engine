using AIPolicyEngine.Api.Models;

namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Validates routing policy rules against known Foundry deployments.
/// One bad rule rejects the whole policy.
/// </summary>
public sealed class RoutingPolicyValidator
{
    private readonly IDeploymentDiscoveryService _deploymentService;

    public RoutingPolicyValidator(IDeploymentDiscoveryService deploymentService)
    {
        _deploymentService = deploymentService;
    }

    public async Task<RoutingPolicyValidationResult> ValidateAsync(
        ModelRoutingPolicy policy, CancellationToken ct = default)
    {
        var deployments = await _deploymentService.GetDeploymentsAsync(ct);
        var knownIds = new HashSet<string>(
            deployments.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        var errors = new List<string>();

        foreach (var rule in policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RequestedDeployment))
            {
                errors.Add("Routing rule has an empty RequestedDeployment.");
            }
            else if (!knownIds.Contains(rule.RequestedDeployment))
            {
                errors.Add(
                    $"Routing rule requested deployment '{rule.RequestedDeployment}' is not a known Foundry deployment.");
            }

            if (!knownIds.Contains(rule.RoutedDeployment))
            {
                errors.Add(
                    $"Routing rule target deployment '{rule.RoutedDeployment}' is not a known Foundry deployment.");
            }
        }

        if (!string.IsNullOrEmpty(policy.FallbackDeployment) &&
            !knownIds.Contains(policy.FallbackDeployment))
        {
            errors.Add(
                $"Fallback deployment '{policy.FallbackDeployment}' is not a known Foundry deployment.");
        }

        return new RoutingPolicyValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }
}

public sealed class RoutingPolicyValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = [];
}
