using Chargeback.Api.Models;

namespace Chargeback.Api.Services;

/// <summary>
/// Evaluates routing decisions based on a <see cref="ModelRoutingPolicy"/>.
/// Pure logic — no external dependencies, fully testable.
/// </summary>
public static class RoutingEvaluator
{
    public static RoutingResult Evaluate(string requestedDeployment, ModelRoutingPolicy? policy)
    {
        if (policy is null || policy.Rules.Count == 0)
            return ApplyDefault(requestedDeployment, policy);

        // Find first matching enabled rule, ordered by priority (lower number = higher priority)
        var match = policy.Rules
            .Where(r => r.Enabled &&
                        string.Equals(r.RequestedDeployment, requestedDeployment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Priority)
            .FirstOrDefault();

        if (match is not null)
        {
            return new RoutingResult
            {
                IsAllowed = true,
                DeploymentId = match.RoutedDeployment,
                WasRouted = true
            };
        }

        return ApplyDefault(requestedDeployment, policy);
    }

    private static RoutingResult ApplyDefault(string requestedDeployment, ModelRoutingPolicy? policy)
    {
        if (policy?.DefaultBehavior == RoutingBehavior.Deny)
        {
            return new RoutingResult
            {
                IsAllowed = false,
                DeploymentId = string.Empty,
                WasRouted = false
            };
        }

        // Passthrough — use fallback if configured, otherwise pass through as-is
        var deployment = !string.IsNullOrEmpty(policy?.FallbackDeployment)
            ? policy!.FallbackDeployment
            : requestedDeployment;

        return new RoutingResult
        {
            IsAllowed = true,
            DeploymentId = deployment,
            WasRouted = !string.Equals(deployment, requestedDeployment, StringComparison.OrdinalIgnoreCase)
        };
    }
}

/// <summary>
/// Result of a routing evaluation.
/// </summary>
public sealed class RoutingResult
{
    public bool IsAllowed { get; set; }
    public string DeploymentId { get; set; } = string.Empty;
    public bool WasRouted { get; set; }
}
