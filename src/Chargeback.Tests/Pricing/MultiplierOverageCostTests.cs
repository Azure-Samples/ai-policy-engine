using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests.Pricing;

/// <summary>
/// B5.5 — Unit tests for <see cref="ChargebackCalculator.CalculateMultiplierOverageCost"/>.
/// Validates overage billing logic: request-based quotas with multiplier billing.
/// Tests boundary conditions, partial overage, and disabled/unlimited scenarios.
/// </summary>
public class MultiplierOverageCostTests
{
    private readonly ChargebackCalculator _calculator = new();

    private static PlanData CreateMultiplierPlan(
        decimal monthlyQuota = 100,
        decimal overageRate = 0.50m,
        bool useMultiplier = true) =>
        new()
        {
            Id = "test-plan",
            Name = "Test Plan",
            UseMultiplierBilling = useMultiplier,
            MonthlyRequestQuota = monthlyQuota,
            OverageRatePerRequest = overageRate
        };

    // ─── Multiplier Billing Disabled ───────────────────────────────

    [Fact]
    public void NoMultiplierBilling_ReturnsZero()
    {
        var plan = CreateMultiplierPlan(useMultiplier: false);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 200m, plan);

        Assert.Equal(0m, cost);
    }

    // ─── Unlimited Quota ───────────────────────────────────────────

    [Fact]
    public void UnlimitedQuota_ReturnsZero()
    {
        var plan = CreateMultiplierPlan(monthlyQuota: 0);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 999_999m, plan);

        Assert.Equal(0m, cost);
    }

    // ─── Within Quota ──────────────────────────────────────────────

    [Theory]
    [InlineData(50, 1.0)]     // well within
    [InlineData(98, 1.0)]     // near limit but still within
    [InlineData(0, 1.0)]      // first request
    public void WithinQuota_NoOverageCost(decimal currentUsage, decimal effectiveCost)
    {
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(effectiveCost, currentUsage, plan);

        Assert.Equal(0m, cost);
    }

    // ─── At Quota Boundary ─────────────────────────────────────────

    [Fact]
    public void AtQuotaBoundary_ExactlyAtLimit_NoOverage()
    {
        // currentUsage=99, effectiveCost=1 → total=100, which is the quota exactly
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 99m, plan);

        Assert.Equal(0m, cost);
    }

    // ─── Over Quota ────────────────────────────────────────────────

    [Fact]
    public void OverQuota_CorrectOverageCalculation()
    {
        // currentUsage=100, effectiveCost=1 → total=101, overage=1 → 1 × $0.50 = $0.50
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 100m, plan);

        Assert.Equal(0.50m, cost);
    }

    // ─── Partial Overage ───────────────────────────────────────────

    [Fact]
    public void PartialOverage_RequestStraddlesQuotaBoundary()
    {
        // currentUsage=99.5, effectiveCost=1 → total=100.5, overage=0.5 → 0.5 × $0.50 = $0.25
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 99.5m, plan);

        Assert.Equal(0.25m, cost);
    }

    // ─── Already Over Quota ────────────────────────────────────────

    [Fact]
    public void AlreadyOverQuota_FullRequestIsOverage()
    {
        // currentUsage=150, effectiveCost=1 → overage capped at effectiveCost=1 → 1 × $0.50 = $0.50
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 1.0m, currentUsage: 150m, plan);

        Assert.Equal(0.50m, cost);
    }

    // ─── Premium Model Over Quota ──────────────────────────────────

    [Fact]
    public void PremiumModel_OverQuota_MultiplierAppliedToOverage()
    {
        // Premium model: effectiveCost=3.0 (3x multiplier), already over quota
        // overage = min(3.0, max(0, 110+3-100)) = min(3.0, 13) = 3.0 → 3.0 × $0.50 = $1.50
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 3.0m, currentUsage: 110m, plan);

        Assert.Equal(1.50m, cost);
    }

    // ─── Cheap Model Partial Overage ───────────────────────────────

    [Fact]
    public void CheapModel_PartialOverage_CorrectFractionalCalc()
    {
        // Mini model: effectiveCost=0.33 (0.33x multiplier), straddles boundary
        // currentUsage=99.9, total=100.23, overage=min(0.33, 0.23) = 0.23 → 0.23 × $0.50 = $0.115 → $0.115
        var plan = CreateMultiplierPlan(monthlyQuota: 100, overageRate: 0.50m);

        var cost = _calculator.CalculateMultiplierOverageCost(
            effectiveCost: 0.33m, currentUsage: 99.9m, plan);

        Assert.Equal(0.115m, cost);
    }
}
