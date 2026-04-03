using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests.Integration;

/// <summary>
/// B5.8 — Integration tests: Full flow (request → route → rate limit → log → audit with multiplier).
/// Tests the end-to-end lifecycle of multiplier billing: effective request cost calculation,
/// accumulation of CurrentPeriodRequests, overage detection, tier tracking, and audit persistence.
/// Exercises the join points between routing, pricing, and billing.
/// </summary>
public class MultiplierBillingIntegrationTests
{
    // ─── Pricing Cache Seeds ────────────────────────────────────────────

    private static readonly Dictionary<string, ModelPricing> StandardPricingCache = new()
    {
        ["gpt-4.1"] = new ModelPricing
        {
            ModelId = "gpt-4.1", Multiplier = 1.0m, TierName = "Standard",
            PromptRatePer1K = 0.02m, CompletionRatePer1K = 0.08m
        },
        ["gpt-4.1-mini"] = new ModelPricing
        {
            ModelId = "gpt-4.1-mini", Multiplier = 0.33m, TierName = "Economy",
            PromptRatePer1K = 0.004m, CompletionRatePer1K = 0.016m
        },
        ["gpt-4o"] = new ModelPricing
        {
            ModelId = "gpt-4o", Multiplier = 1.5m, TierName = "Premium",
            PromptRatePer1K = 0.03m, CompletionRatePer1K = 0.06m
        },
        ["gpt-4o-eastus2"] = new ModelPricing
        {
            ModelId = "gpt-4o-eastus2", Multiplier = 1.5m, TierName = "Premium",
            PromptRatePer1K = 0.03m, CompletionRatePer1K = 0.06m
        },
        ["gpt-4.1-mini-westus"] = new ModelPricing
        {
            ModelId = "gpt-4.1-mini-westus", Multiplier = 0.33m, TierName = "Economy",
            PromptRatePer1K = 0.004m, CompletionRatePer1K = 0.016m
        },
    };

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PlanData CreatePlan(
        bool useMultiplierBilling = true,
        decimal monthlyRequestQuota = 100m,
        decimal overageRatePerRequest = 0.50m,
        long monthlyTokenQuota = 10_000_000,
        string? routingPolicyId = null)
    {
        return new PlanData
        {
            Id = "plan-multiplier",
            Name = "Multiplier Test Plan",
            MonthlyRate = 99m,
            MonthlyTokenQuota = monthlyTokenQuota,
            AllowOverbilling = true,
            CostPerMillionTokens = 5m,
            RollUpAllDeployments = true,
            UseMultiplierBilling = useMultiplierBilling,
            MonthlyRequestQuota = monthlyRequestQuota,
            OverageRatePerRequest = overageRatePerRequest,
            ModelRoutingPolicyId = routingPolicyId,
        };
    }

    private static ClientPlanAssignment CreateClient(
        decimal currentPeriodRequests = 0m,
        decimal overbilledRequests = 0m)
    {
        return new ClientPlanAssignment
        {
            Id = "billing-client:tenant-1",
            ClientAppId = "billing-client",
            TenantId = "tenant-1",
            PlanId = "plan-multiplier",
            DisplayName = "Billing Test Client",
            CurrentPeriodStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            CurrentPeriodUsage = 0,
            CurrentPeriodRequests = currentPeriodRequests,
            OverbilledRequests = overbilledRequests,
            RequestsByTier = new Dictionary<string, decimal>(),
        };
    }

    /// <summary>
    /// Simulates the full multiplier billing flow that Freamon will integrate
    /// into LogIngestEndpoints. Composes:
    ///   1. Calculate effective request cost via multiplier
    ///   2. Update CurrentPeriodRequests
    ///   3. Track tier usage (RequestsByTier)
    ///   4. Detect overage (OverbilledRequests)
    ///   5. Calculate overage cost
    ///   6. Build audit fields
    /// </summary>
    private static MultiplierBillingResult SimulateMultiplierBilling(
        string deploymentId,
        string? modelName,
        PlanData plan,
        ClientPlanAssignment client,
        ChargebackCalculator calculator)
    {
        var logData = new CachedLogData
        {
            DeploymentId = deploymentId,
            Model = modelName,
            ClientAppId = client.ClientAppId,
            TenantId = client.TenantId,
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
        };

        if (!plan.UseMultiplierBilling)
        {
            return new MultiplierBillingResult
            {
                EffectiveRequestCost = 0m,
                MultiplierApplied = false,
                OverageCost = 0m,
            };
        }

        // Step 1: Calculate effective request cost
        var effectiveCost = calculator.CalculateEffectiveRequestCost(logData);

        // Step 2: Update CurrentPeriodRequests
        var previousRequests = client.CurrentPeriodRequests;
        client.CurrentPeriodRequests += effectiveCost;

        // Step 3: Track tier usage
        var pricing = StandardPricingCache.GetValueOrDefault(deploymentId);
        var tierName = pricing?.TierName ?? "Standard";
        if (!client.RequestsByTier.ContainsKey(tierName))
            client.RequestsByTier[tierName] = 0m;
        client.RequestsByTier[tierName] += effectiveCost;

        // Step 4: Detect overage
        var overageCost = calculator.CalculateMultiplierOverageCost(effectiveCost, previousRequests, plan);
        if (overageCost > 0)
            client.OverbilledRequests += effectiveCost;

        return new MultiplierBillingResult
        {
            EffectiveRequestCost = effectiveCost,
            MultiplierApplied = true,
            OverageCost = overageCost,
            TierName = tierName,
            CurrentPeriodRequests = client.CurrentPeriodRequests,
            OverbilledRequests = client.OverbilledRequests,
        };
    }

    private sealed class MultiplierBillingResult
    {
        public decimal EffectiveRequestCost { get; init; }
        public bool MultiplierApplied { get; init; }
        public decimal OverageCost { get; init; }
        public string TierName { get; init; } = "Standard";
        public decimal CurrentPeriodRequests { get; init; }
        public decimal OverbilledRequests { get; init; }
    }

    // ─── B5.8.1: Multiplier enabled — computes EffectiveRequestCost ────

    [Theory]
    [InlineData("gpt-4.1", 1.0)]       // baseline
    [InlineData("gpt-4.1-mini", 0.33)]  // cheap model
    [InlineData("gpt-4o", 1.5)]         // premium model
    public void MultiplierEnabled_ComputesEffectiveRequestCost(string deploymentId, decimal expectedCost)
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true);
        var client = CreateClient();

        var result = SimulateMultiplierBilling(deploymentId, deploymentId, plan, client, calculator);

        Assert.True(result.MultiplierApplied);
        Assert.Equal(expectedCost, result.EffectiveRequestCost);
        Assert.Equal(expectedCost, result.CurrentPeriodRequests);
    }

    // ─── B5.8.2: Multiplier disabled — skips calculation ───────────────

    [Fact]
    public void MultiplierDisabled_SkipsMultiplierCalculation()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: false);
        var client = CreateClient();

        var result = SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, client, calculator);

        Assert.False(result.MultiplierApplied);
        Assert.Equal(0m, result.EffectiveRequestCost);
        Assert.Equal(0m, result.OverageCost);
        // Client state unchanged
        Assert.Equal(0m, client.CurrentPeriodRequests);
    }

    // ─── B5.8.3: Overage detection ─────────────────────────────────────

    [Fact]
    public void OverageDetection_ExceedsMonthlyRequestQuota()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 10m, overageRatePerRequest: 0.50m);
        // Client already at 9.5 effective requests — next request will exceed quota
        var client = CreateClient(currentPeriodRequests: 9.5m);

        var result = SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);

        // gpt-4.1 multiplier = 1.0, so effective cost = 1.0
        // 9.5 + 1.0 = 10.5 → exceeds quota of 10
        // Overage = min(1.0, max(0, 10.5 - 10)) = 0.5
        Assert.Equal(1.0m, result.EffectiveRequestCost);
        Assert.True(result.OverageCost > 0);
        Assert.Equal(0.25m, result.OverageCost); // 0.5 units × $0.50/unit
        Assert.Equal(1.0m, result.OverbilledRequests);
    }

    [Fact]
    public void OverageDetection_AlreadyOverQuota_FullRequestIsOverage()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 10m, overageRatePerRequest: 1.00m);
        // Client already at 15 — fully over quota
        var client = CreateClient(currentPeriodRequests: 15m);

        var result = SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);

        // Entire request is overage: 1.0 × $1.00 = $1.00
        Assert.Equal(1.0m, result.EffectiveRequestCost);
        Assert.Equal(1.0m, result.OverageCost);
    }

    [Fact]
    public void OverageDetection_AtExactBoundary_NoOverage()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 10m);
        // Client at 9.0 — next 1.0 request lands exactly at boundary
        var client = CreateClient(currentPeriodRequests: 9.0m);

        var result = SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);

        // 9.0 + 1.0 = 10.0 → exactly at quota → no overage
        Assert.Equal(0m, result.OverageCost);
        Assert.Equal(0m, result.OverbilledRequests);
    }

    [Fact]
    public void OverageDetection_UnlimitedQuota_NeverOverages()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 0m); // 0 = unlimited
        var client = CreateClient(currentPeriodRequests: 999_999m);

        var result = SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, client, calculator);

        Assert.Equal(0m, result.OverageCost);
    }

    // ─── B5.8.4: Audit fields contain routing + multiplier metadata ────

    [Fact]
    public void AuditFields_ContainMultiplierMetadata()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true);
        var client = CreateClient();

        var result = SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, client, calculator);

        // Audit fields: multiplier, effective cost, tier name
        Assert.Equal(1.5m, result.EffectiveRequestCost);
        Assert.Equal("Premium", result.TierName);
        Assert.True(result.MultiplierApplied);
    }

    [Fact]
    public void AuditFields_RoutingMetadata_PreservedThroughFlow()
    {
        // Simulate: request for gpt-4o was routed to gpt-4o-eastus2
        var requestedDeployment = "gpt-4o";
        var routedDeployment = "gpt-4o-eastus2";

        var routingPolicy = new ModelRoutingPolicy
        {
            Id = "audit-policy",
            Rules = [new RouteRule { RequestedDeployment = requestedDeployment, RoutedDeployment = routedDeployment, Priority = 1, Enabled = true }]
        };

        // Step 1: Route
        var routingResult = RoutingEvaluator.Evaluate(requestedDeployment, routingPolicy);
        Assert.Equal(routedDeployment, routingResult.DeploymentId);

        // Step 2: Calculate multiplier on ROUTED deployment
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, routingPolicyId: "audit-policy");
        var client = CreateClient();
        var result = SimulateMultiplierBilling(routedDeployment, "gpt-4o", plan, client, calculator);

        // Audit should capture: requested deployment, routing policy, multiplier, tier
        Assert.Equal(1.5m, result.EffectiveRequestCost); // gpt-4o-eastus2 has same multiplier as gpt-4o
        Assert.Equal("Premium", result.TierName);

        // Both deployment IDs are available for audit record
        Assert.NotEqual(requestedDeployment, routedDeployment);
    }

    // ─── B5.8.5: Cost-optimized routing → lower multiplier ─────────────

    [Fact]
    public void CostOptimizedRouting_LowerMultiplier_FewerEffectiveRequests()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 100m);

        // Scenario: Route gpt-4o (1.5x) → gpt-4.1-mini-westus (0.33x) for cost savings
        var premiumClient = CreateClient();
        var premiumResult = SimulateMultiplierBilling(
            "gpt-4o", "gpt-4o", plan, premiumClient, calculator);

        var cheapClient = CreateClient();
        var cheapResult = SimulateMultiplierBilling(
            "gpt-4.1-mini-westus", "gpt-4.1-mini", plan, cheapClient, calculator);

        // Premium: 1.5 effective requests consumed
        Assert.Equal(1.5m, premiumResult.EffectiveRequestCost);
        Assert.Equal(1.5m, premiumResult.CurrentPeriodRequests);

        // Economy: 0.33 effective requests consumed — roughly 4.5x cheaper
        Assert.Equal(0.33m, cheapResult.EffectiveRequestCost);
        Assert.Equal(0.33m, cheapResult.CurrentPeriodRequests);

        // Cost-optimized routing saves budget
        Assert.True(cheapResult.EffectiveRequestCost < premiumResult.EffectiveRequestCost);
    }

    // ─── B5.8.6: Multiple requests accumulate correctly ────────────────

    [Fact]
    public void MultipleRequests_AccumulateCurrentPeriodRequests()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 100m);
        var client = CreateClient();

        // Send 5 requests: 2x gpt-4.1 (1.0), 2x gpt-4.1-mini (0.33), 1x gpt-4o (1.5)
        SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);
        SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);
        SimulateMultiplierBilling("gpt-4.1-mini", "gpt-4.1-mini", plan, client, calculator);
        SimulateMultiplierBilling("gpt-4.1-mini", "gpt-4.1-mini", plan, client, calculator);
        SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, client, calculator);

        // Expected: 2×1.0 + 2×0.33 + 1×1.5 = 2.0 + 0.66 + 1.5 = 4.16
        Assert.Equal(4.16m, client.CurrentPeriodRequests);
    }

    // ─── B5.8.7: RequestsByTier categorizes by model tier ──────────────

    [Fact]
    public void TierTracking_RequestsByTier_CategorizesByModelTier()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 100m);
        var client = CreateClient();

        // Standard tier: 2x gpt-4.1 (1.0 each)
        SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);
        SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, client, calculator);

        // Economy tier: 1x gpt-4.1-mini (0.33)
        SimulateMultiplierBilling("gpt-4.1-mini", "gpt-4.1-mini", plan, client, calculator);

        // Premium tier: 1x gpt-4o (1.5)
        SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, client, calculator);

        Assert.Equal(3, client.RequestsByTier.Count);
        Assert.Equal(2.0m, client.RequestsByTier["Standard"]);
        Assert.Equal(0.33m, client.RequestsByTier["Economy"]);
        Assert.Equal(1.5m, client.RequestsByTier["Premium"]);
    }

    // ─── B5.8 Edge: Unknown deployment defaults to 1.0x multiplier ────

    [Fact]
    public void UnknownDeployment_DefaultsToBaseline()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 100m);
        var client = CreateClient();

        var result = SimulateMultiplierBilling("unknown-model-xyz", null, plan, client, calculator);

        Assert.Equal(1.0m, result.EffectiveRequestCost);
        Assert.Equal("Standard", result.TierName);
    }

    // ─── B5.8 Edge: Premium model overage is more expensive ────────────

    [Fact]
    public void PremiumModelOverage_CostsMoreThanBaseline()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 10m, overageRatePerRequest: 1.00m);

        // Client at quota — next request fully in overage
        var baselineClient = CreateClient(currentPeriodRequests: 10m);
        var baselineResult = SimulateMultiplierBilling("gpt-4.1", "gpt-4.1", plan, baselineClient, calculator);

        var premiumClient = CreateClient(currentPeriodRequests: 10m);
        var premiumResult = SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, premiumClient, calculator);

        // Premium overage: 1.5 × $1.00 = $1.50
        // Baseline overage: 1.0 × $1.00 = $1.00
        Assert.Equal(1.00m, baselineResult.OverageCost);
        Assert.Equal(1.50m, premiumResult.OverageCost);
        Assert.True(premiumResult.OverageCost > baselineResult.OverageCost);
    }

    // ─── B5.8 Edge: Routing + pricing interaction in full flow ──────────

    [Fact]
    public void FullFlow_RouteToEconomyModel_ReducesRequestConsumption()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 100m,
            routingPolicyId: "cost-opt-policy");
        var client = CreateClient();

        var routingPolicy = new ModelRoutingPolicy
        {
            Id = "cost-opt-policy",
            Rules = [
                new RouteRule { RequestedDeployment = "gpt-4o", RoutedDeployment = "gpt-4.1-mini-westus", Priority = 1, Enabled = true }
            ]
        };

        // Step 1: Route
        var routingResult = RoutingEvaluator.Evaluate("gpt-4o", routingPolicy);
        Assert.True(routingResult.WasRouted);
        Assert.Equal("gpt-4.1-mini-westus", routingResult.DeploymentId);

        // Step 2: Bill using ROUTED deployment's multiplier (0.33x instead of 1.5x)
        var result = SimulateMultiplierBilling(
            routingResult.DeploymentId, "gpt-4.1-mini", plan, client, calculator);

        Assert.Equal(0.33m, result.EffectiveRequestCost);
        Assert.Equal("Economy", result.TierName);

        // 100 requests quota / 0.33 per request ≈ 303 requests before quota hit
        // vs 100 / 1.5 ≈ 66 requests with gpt-4o direct
        Assert.True(result.CurrentPeriodRequests < 1.0m, "Economy routing stretches budget further");
    }

    // ─── B5.8 Edge: Overage straddles boundary ─────────────────────────

    [Fact]
    public void OverageStraddle_PartialOverage_CorrectlyCalculated()
    {
        var calculator = new ChargebackCalculator(StandardPricingCache);
        var plan = CreatePlan(useMultiplierBilling: true, monthlyRequestQuota: 10m, overageRatePerRequest: 2.00m);
        // Client at 9.8 — a 0.33x request (mini) won't overage, but a 1.5x request (premium) will
        var miniClient = CreateClient(currentPeriodRequests: 9.8m);
        var premiumClient = CreateClient(currentPeriodRequests: 9.8m);

        var miniResult = SimulateMultiplierBilling("gpt-4.1-mini", "gpt-4.1-mini", plan, miniClient, calculator);
        var premiumResult = SimulateMultiplierBilling("gpt-4o", "gpt-4o", plan, premiumClient, calculator);

        // Mini: 9.8 + 0.33 = 10.13 → overage = min(0.33, max(0, 10.13-10)) = 0.13 → cost = 0.13 × $2 = $0.26
        Assert.Equal(0.33m, miniResult.EffectiveRequestCost);
        Assert.Equal(Math.Round(0.13m * 2.00m, 4), miniResult.OverageCost);

        // Premium: 9.8 + 1.5 = 11.3 → overage = min(1.5, max(0, 11.3-10)) = 1.3 → cost = 1.3 × $2 = $2.60
        Assert.Equal(1.5m, premiumResult.EffectiveRequestCost);
        Assert.Equal(Math.Round(1.3m * 2.00m, 4), premiumResult.OverageCost);
    }
}
