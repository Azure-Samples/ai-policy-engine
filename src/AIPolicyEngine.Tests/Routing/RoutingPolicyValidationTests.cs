using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using NSubstitute;

namespace AIPolicyEngine.Tests.Routing;

/// <summary>
/// B5.6 — Unit tests for Foundry deployment validation in routing policy CRUD.
/// Uses <see cref="RoutingPolicyValidator"/> with a mocked <see cref="IDeploymentDiscoveryService"/>.
/// One bad deployment rejects the whole policy.
/// </summary>
public class RoutingPolicyValidationTests
{
    private readonly IDeploymentDiscoveryService _deploymentService;
    private readonly RoutingPolicyValidator _validator;

    public RoutingPolicyValidationTests()
    {
        _deploymentService = Substitute.For<IDeploymentDiscoveryService>();
        _validator = new RoutingPolicyValidator(_deploymentService);
    }

    private void SetKnownDeployments(params string[] deploymentIds)
    {
        var deployments = deploymentIds.Select(id => new DeploymentInfo
        {
            Id = id,
            Model = $"model-{id}",
            ModelVersion = "2024-01-01"
        }).ToList();

        _deploymentService.GetDeploymentsAsync(Arg.Any<CancellationToken>())
            .Returns(deployments);
    }

    // ─── Valid Deployment ───────────────────────────────────────────

    [Fact]
    public async Task ValidDeployment_KnownFoundryDeployment_PassesValidation()
    {
        SetKnownDeployments("gpt-4", "gpt-4o", "gpt-4.1-mini");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4o",
                    Enabled = true
                }
            ]
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ─── Invalid Deployment ────────────────────────────────────────

    [Fact]
    public async Task InvalidDeployment_UnknownFoundryDeployment_FailsValidation()
    {
        SetKnownDeployments("gpt-4", "gpt-4o");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "nonexistent-deployment",
                    Enabled = true
                }
            ]
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("nonexistent-deployment", result.Errors[0]);
    }

    // ─── Fallback Deployment Validation ────────────────────────────

    [Fact]
    public async Task FallbackDeployment_MustBeKnownDeployment()
    {
        SetKnownDeployments("gpt-4", "gpt-4o");

        var policy = new ModelRoutingPolicy
        {
            Rules = [],
            FallbackDeployment = "unknown-fallback"
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("unknown-fallback", result.Errors[0]);
    }

    [Fact]
    public async Task FallbackDeployment_KnownDeployment_PassesValidation()
    {
        SetKnownDeployments("gpt-4", "gpt-4o");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4o",
                    Enabled = true
                }
            ],
            FallbackDeployment = "gpt-4"
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ─── Mixed Valid/Invalid ───────────────────────────────────────

    [Fact]
    public async Task MixedValidInvalid_OneBadRule_RejectsWholePolicy()
    {
        SetKnownDeployments("gpt-4", "gpt-4o", "gpt-4.1-mini");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4o",   // valid
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-4o",
                    RoutedDeployment = "gpt-4.1-mini",   // valid
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "gpt-3",
                    RoutedDeployment = "totally-fake",   // INVALID
                    Enabled = true
                }
            ]
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("gpt-3"));
        Assert.Contains(result.Errors, e => e.Contains("totally-fake"));
    }

    // ─── Empty Deployment List From Foundry ─────────────────────────

    [Fact]
    public async Task EmptyDeploymentList_AllRoutingRulesFailValidation()
    {
        // Foundry returns no deployments — nothing is valid
        SetKnownDeployments(); // empty

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4o",
                    Enabled = true
                }
            ]
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // ─── Multiple Invalid Deployments ──────────────────────────────

    [Fact]
    public async Task MultipleInvalid_ReportsAllErrors()
    {
        SetKnownDeployments("gpt-4");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "a",
                    RoutedDeployment = "fake-1",
                    Enabled = true
                },
                new RouteRule
                {
                    RequestedDeployment = "b",
                    RoutedDeployment = "fake-2",
                    Enabled = true
                }
            ],
            FallbackDeployment = "fake-fallback"
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.False(result.IsValid);
        Assert.Equal(5, result.Errors.Count); // 2 rules × 2 (requested + routed) + 1 fallback
    }

    // ─── Case Insensitive Matching ─────────────────────────────────

    [Fact]
    public async Task CaseInsensitive_DeploymentMatchingIsNotCaseSensitive()
    {
        SetKnownDeployments("GPT-4", "gpt-4o");

        var policy = new ModelRoutingPolicy
        {
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4",
                    RoutedDeployment = "gpt-4",   // different case than "GPT-4"
                    Enabled = true
                }
            ]
        };

        var result = await _validator.ValidateAsync(policy);

        Assert.True(result.IsValid);
    }
}
