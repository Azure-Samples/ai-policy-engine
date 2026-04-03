using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests.Pricing;

/// <summary>
/// B5.4 — Unit tests for <see cref="ChargebackCalculator.CalculateEffectiveRequestCost"/>.
/// Validates per-request multiplier pricing: effective_cost = 1 × model_multiplier.
/// Tests multiplier lookup by deploymentId, model fallback, and invalid multiplier handling.
/// </summary>
public class EffectiveRequestCostTests
{
    private static ChargebackCalculator CreateCalculatorWithPricing(
        params (string modelId, decimal multiplier)[] models)
    {
        var cache = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modelId, multiplier) in models)
        {
            cache[modelId] = new ModelPricing
            {
                ModelId = modelId,
                Multiplier = multiplier,
                PromptRatePer1K = 0.02m,
                CompletionRatePer1K = 0.08m
            };
        }
        return new ChargebackCalculator(cache);
    }

    // ─── Standard Multipliers ──────────────────────────────────────

    [Theory]
    [InlineData("gpt-4.1", 1.0, 1.0)]         // Baseline: multiplier 1.0 → cost = 1.0
    [InlineData("gpt-4.1-mini", 0.33, 0.33)]   // Cheap model: multiplier 0.33 → cost = 0.33
    [InlineData("gpt-4-premium", 3.0, 3.0)]    // Premium model: multiplier 3.0 → cost = 3.0
    public void CalculateEffectiveRequestCost_WithMultiplier_ReturnsExpected(
        string deploymentId, decimal multiplier, decimal expected)
    {
        var calculator = CreateCalculatorWithPricing((deploymentId, multiplier));
        var logData = new CachedLogData { DeploymentId = deploymentId };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(expected, cost);
    }

    // ─── Zero Multiplier → Default to 1.0 ──────────────────────────

    [Fact]
    public void CalculateEffectiveRequestCost_ZeroMultiplier_DefaultsToOne()
    {
        var calculator = CreateCalculatorWithPricing(("zero-model", 0m));
        var logData = new CachedLogData { DeploymentId = "zero-model" };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(1.0m, cost);
    }

    // ─── Negative Multiplier → Default to 1.0 ──────────────────────

    [Fact]
    public void CalculateEffectiveRequestCost_NegativeMultiplier_DefaultsToOne()
    {
        var calculator = CreateCalculatorWithPricing(("negative-model", -2.5m));
        var logData = new CachedLogData { DeploymentId = "negative-model" };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(1.0m, cost);
    }

    // ─── Unknown Deployment → Default to 1.0 ───────────────────────

    [Fact]
    public void CalculateEffectiveRequestCost_UnknownDeployment_DefaultsToOne()
    {
        var calculator = CreateCalculatorWithPricing(("gpt-4.1", 1.0m));
        var logData = new CachedLogData { DeploymentId = "totally-unknown-model-v99" };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(1.0m, cost);
    }

    // ─── Model Fallback ────────────────────────────────────────────

    [Fact]
    public void CalculateEffectiveRequestCost_DeploymentNotFound_FallsBackToModelName()
    {
        // Pricing is configured for the model name "gpt-4.1-mini", not the deployment ID
        var calculator = CreateCalculatorWithPricing(("gpt-4.1-mini", 0.33m));
        var logData = new CachedLogData
        {
            DeploymentId = "my-custom-deployment",
            Model = "gpt-4.1-mini"
        };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(0.33m, cost);
    }

    // ─── Empty Cache ───────────────────────────────────────────────

    [Fact]
    public void CalculateEffectiveRequestCost_EmptyPricingCache_DefaultsToOne()
    {
        var calculator = new ChargebackCalculator();
        var logData = new CachedLogData { DeploymentId = "gpt-4.1" };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        Assert.Equal(1.0m, cost);
    }

    // ─── Deployment ID Takes Priority Over Model Name ──────────────

    [Fact]
    public void CalculateEffectiveRequestCost_DeploymentIdMatchFirst_IgnoresModel()
    {
        var calculator = CreateCalculatorWithPricing(
            ("my-deployment", 2.0m),
            ("gpt-4.1-mini", 0.33m));
        var logData = new CachedLogData
        {
            DeploymentId = "my-deployment",
            Model = "gpt-4.1-mini"
        };

        var cost = calculator.CalculateEffectiveRequestCost(logData);

        // Should use the deployment match (2.0), not the model match (0.33)
        Assert.Equal(2.0m, cost);
    }
}
