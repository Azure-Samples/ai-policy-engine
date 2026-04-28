using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;

namespace AIPolicyEngine.Tests.Routing;

/// <summary>
/// B5.3 — Unit tests for routing rule matching via <see cref="RoutingEvaluator"/>.
/// Tests exact-match routing on Foundry deployment IDs, priority ordering,
/// enabled/disabled rules, default behaviors, and fallback handling.
/// </summary>
public class RoutingEvaluatorTests
{
    // ─── Exact Match ───────────────────────────────────────────────

    [Fact]
    public void ExactMatch_RuleForGpt4_MatchesGpt4()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-production",
                    Priority = 1,
                    Enabled = true
                }
            ]
        };

        var result = RoutingEvaluator.Evaluate("gpt-4", policy);

        Assert.True(result.IsAllowed);
        Assert.True(result.WasRouted);
        Assert.Equal("gpt-4-production", result.DeploymentId);
    }

    // ─── No Match ──────────────────────────────────────────────────

    [Fact]
    public void NoMatch_RuleForGpt4_DoesNotMatchGpt4o()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-production",
                    Priority = 1,
                    Enabled = true
                }
            ],
            DefaultBehavior = RoutingBehavior.Passthrough
        };

        var result = RoutingEvaluator.Evaluate("gpt-4o", policy);

        Assert.True(result.IsAllowed);
        Assert.False(result.WasRouted);
        Assert.Equal("gpt-4o", result.DeploymentId);
    }

    // ─── Priority Ordering ─────────────────────────────────────────

    [Fact]
    public void PriorityOrdering_LowerPriorityNumberWins()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-low-priority",
                    Priority = 10,
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-high-priority",
                    Priority = 1,
                    Enabled = true
                }
            ]
        };

        var result = RoutingEvaluator.Evaluate("gpt-4", policy);

        Assert.True(result.IsAllowed);
        Assert.True(result.WasRouted);
        Assert.Equal("gpt-4-high-priority", result.DeploymentId);
    }

    // ─── Enabled/Disabled ──────────────────────────────────────────

    [Fact]
    public void DisabledRule_IsSkipped_FallsToNextMatch()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-disabled",
                    Priority = 1,
                    Enabled = false
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-enabled",
                    Priority = 2,
                    Enabled = true
                }
            ]
        };

        var result = RoutingEvaluator.Evaluate("gpt-4", policy);

        Assert.True(result.IsAllowed);
        Assert.True(result.WasRouted);
        Assert.Equal("gpt-4-enabled", result.DeploymentId);
    }

    // ─── Default Behavior: Passthrough ─────────────────────────────

    [Fact]
    public void DefaultPassthrough_UnmatchedDeployment_PassesThrough()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-prod",
                    Priority = 1,
                    Enabled = true
                }
            ],
            DefaultBehavior = RoutingBehavior.Passthrough
        };

        var result = RoutingEvaluator.Evaluate("gpt-35-turbo", policy);

        Assert.True(result.IsAllowed);
        Assert.False(result.WasRouted);
        Assert.Equal("gpt-35-turbo", result.DeploymentId);
    }

    // ─── Default Behavior: Deny ────────────────────────────────────

    [Fact]
    public void DefaultDeny_UnmatchedDeployment_ReturnsDenied()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-prod",
                    Priority = 1,
                    Enabled = true
                }
            ],
            DefaultBehavior = RoutingBehavior.Deny
        };

        var result = RoutingEvaluator.Evaluate("gpt-35-turbo", policy);

        Assert.False(result.IsAllowed);
        Assert.False(result.WasRouted);
        Assert.Equal(string.Empty, result.DeploymentId);
    }

    // ─── Empty Rules ───────────────────────────────────────────────

    [Theory]
    [InlineData(RoutingBehavior.Passthrough)]
    [InlineData(RoutingBehavior.Deny)]
    public void EmptyRules_FallsThroughToDefaultBehavior(RoutingBehavior behavior)
    {
        var policy = new ModelRoutingPolicy
        {
            Rules = [],
            DefaultBehavior = behavior
        };

        var result = RoutingEvaluator.Evaluate("gpt-4o", policy);

        if (behavior == RoutingBehavior.Passthrough)
        {
            Assert.True(result.IsAllowed);
            Assert.Equal("gpt-4o", result.DeploymentId);
        }
        else
        {
            Assert.False(result.IsAllowed);
        }
    }

    // ─── Null Policy ───────────────────────────────────────────────

    [Fact]
    public void NullPolicy_PassesThroughUnchanged()
    {
        var result = RoutingEvaluator.Evaluate("gpt-4o", null);

        Assert.True(result.IsAllowed);
        Assert.False(result.WasRouted);
        Assert.Equal("gpt-4o", result.DeploymentId);
    }

    // ─── Fallback Deployment ───────────────────────────────────────

    [Fact]
    public void FallbackDeployment_WhenNoMatchAndPassthrough_UsesFallback()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-prod",
                    Priority = 1,
                    Enabled = true
                }
            ],
            DefaultBehavior = RoutingBehavior.Passthrough,
            FallbackDeployment = "gpt-4o-fallback"
        };

        var result = RoutingEvaluator.Evaluate("unknown-model", policy);

        Assert.True(result.IsAllowed);
        Assert.True(result.WasRouted);
        Assert.Equal("gpt-4o-fallback", result.DeploymentId);
    }

    [Fact]
    public void FallbackDeployment_WhenNoMatchAndDeny_FallbackIgnored()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules = [],
            DefaultBehavior = RoutingBehavior.Deny,
            FallbackDeployment = "gpt-4o-fallback"
        };

        var result = RoutingEvaluator.Evaluate("unknown-model", policy);

        Assert.False(result.IsAllowed);
        Assert.Equal(string.Empty, result.DeploymentId);
    }

    // ─── Multiple Rules ────────────────────────────────────────────

    [Fact]
    public void MultipleRules_FirstMatchingEnabledRuleWins()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-team-a",
                    Priority = 5,
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-4o",
                    RoutedDeployment = "gpt-4o-premium",
                    Priority = 1,
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-4o",
                    RoutedDeployment = "gpt-4o-standard",
                    Priority = 10,
                    Enabled = true
                }
            ]
        };

        // gpt-4 → gpt-4-team-a (only match)
        var result1 = RoutingEvaluator.Evaluate("gpt-4", policy);
        Assert.Equal("gpt-4-team-a", result1.DeploymentId);

        // gpt-4o → gpt-4o-premium (lower priority wins)
        var result2 = RoutingEvaluator.Evaluate("gpt-4o", policy);
        Assert.Equal("gpt-4o-premium", result2.DeploymentId);
    }

    [Fact]
    public void AllRulesDisabled_FallsToDefault()
    {
        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4-prod",
                    Priority = 1,
                    Enabled = false
                }
            ],
            DefaultBehavior = RoutingBehavior.Deny
        };

        var result = RoutingEvaluator.Evaluate("gpt-4", policy);

        Assert.False(result.IsAllowed);
    }
}
