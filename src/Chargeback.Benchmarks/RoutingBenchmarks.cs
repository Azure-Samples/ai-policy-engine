using BenchmarkDotNet.Attributes;
using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Benchmarks;

/// <summary>
/// B5.10 — Routing decision latency benchmarks.
/// Measures the added latency of routing evaluation in the precheck path.
/// Target: &lt;5ms p99 added latency for routing evaluation.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RoutingBenchmarks
{
    private ModelRoutingPolicy _singleRulePolicy = null!;
    private ModelRoutingPolicy _tenRulePolicy = null!;
    private ModelRoutingPolicy _noMatchPolicy = null!;
    private ChargebackCalculator _calculator = null!;
    private CachedLogData _baselineLogData = null!;
    private CachedLogData _premiumLogData = null!;
    private CachedLogData _unknownLogData = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Single rule: exact match on "gpt-4o" → "gpt-4o-east"
        _singleRulePolicy = new ModelRoutingPolicy
        {
            Id = "bench-single",
            Name = "Single Rule",
            DefaultBehavior = RoutingBehavior.Passthrough,
            Rules =
            [
                new RouteRule
                {
                    RequestedDeployment = "gpt-4o",
                    RoutedDeployment = "gpt-4o-east",
                    Priority = 1,
                    Enabled = true
                }
            ]
        };

        // 10 rules: the LAST rule matches "gpt-4o" (worst case scan)
        _tenRulePolicy = new ModelRoutingPolicy
        {
            Id = "bench-ten",
            Name = "Ten Rules",
            DefaultBehavior = RoutingBehavior.Passthrough,
            Rules = Enumerable.Range(1, 9)
                .Select(i => new RouteRule
                {
                    RequestedDeployment = $"model-{i}",
                    RoutedDeployment = $"model-{i}-routed",
                    Priority = i,
                    Enabled = true
                })
                .Append(new RouteRule
                {
                    RequestedDeployment = "gpt-4o",
                    RoutedDeployment = "gpt-4o-west",
                    Priority = 10,
                    Enabled = true
                })
                .ToList()
        };

        // No match: request a deployment that doesn't match any rule → Passthrough
        _noMatchPolicy = new ModelRoutingPolicy
        {
            Id = "bench-nomatch",
            Name = "No Match",
            DefaultBehavior = RoutingBehavior.Passthrough,
            Rules = Enumerable.Range(1, 10)
                .Select(i => new RouteRule
                {
                    RequestedDeployment = $"other-model-{i}",
                    RoutedDeployment = $"other-model-{i}-routed",
                    Priority = i,
                    Enabled = true
                })
                .ToList()
        };

        // Calculator with pricing cache for CalculateEffectiveRequestCost
        _calculator = new ChargebackCalculator(new Dictionary<string, ModelPricing>
        {
            ["gpt-4o"] = new()
            {
                ModelId = "gpt-4o",
                Multiplier = 1.0m,
                TierName = "Standard",
                PromptRatePer1K = 0.03m,
                CompletionRatePer1K = 0.06m
            },
            ["gpt-4o-mini"] = new()
            {
                ModelId = "gpt-4o-mini",
                Multiplier = 0.33m,
                TierName = "Economy",
                PromptRatePer1K = 0.005m,
                CompletionRatePer1K = 0.015m
            },
            ["gpt-4-premium"] = new()
            {
                ModelId = "gpt-4-premium",
                Multiplier = 3.0m,
                TierName = "Premium",
                PromptRatePer1K = 0.02m,
                CompletionRatePer1K = 0.08m
            }
        });

        _baselineLogData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            Model = "gpt-4o",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700
        };

        _premiumLogData = new CachedLogData
        {
            DeploymentId = "gpt-4-premium",
            Model = "gpt-4-premium",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700
        };

        _unknownLogData = new CachedLogData
        {
            DeploymentId = "unknown-model-xyz",
            Model = "unknown-model-xyz",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Routing evaluation benchmarks
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Baseline = true, Description = "Precheck: No routing policy (baseline)")]
    public RoutingResult Routing_NoPolicy()
    {
        return RoutingEvaluator.Evaluate("gpt-4o", null);
    }

    [Benchmark(Description = "Precheck: 1 rule, exact match")]
    public RoutingResult Routing_SingleRule_ExactMatch()
    {
        return RoutingEvaluator.Evaluate("gpt-4o", _singleRulePolicy);
    }

    [Benchmark(Description = "Precheck: 10 rules, last rule matches")]
    public RoutingResult Routing_TenRules_LastMatch()
    {
        return RoutingEvaluator.Evaluate("gpt-4o", _tenRulePolicy);
    }

    [Benchmark(Description = "Precheck: 10 rules, no match (Passthrough)")]
    public RoutingResult Routing_NoMatch_Passthrough()
    {
        return RoutingEvaluator.Evaluate("gpt-4o", _noMatchPolicy);
    }

    // ═══════════════════════════════════════════════════════════════
    // CalculateEffectiveRequestCost benchmarks
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "EffectiveRequestCost: known model (1.0x)")]
    public decimal EffectiveRequestCost_Baseline()
    {
        return _calculator.CalculateEffectiveRequestCost(_baselineLogData);
    }

    [Benchmark(Description = "EffectiveRequestCost: premium model (3.0x)")]
    public decimal EffectiveRequestCost_Premium()
    {
        return _calculator.CalculateEffectiveRequestCost(_premiumLogData);
    }

    [Benchmark(Description = "EffectiveRequestCost: unknown model (default 1.0x)")]
    public decimal EffectiveRequestCost_Unknown()
    {
        return _calculator.CalculateEffectiveRequestCost(_unknownLogData);
    }

    // ═══════════════════════════════════════════════════════════════
    // CalculateMultiplierOverageCost benchmarks
    // ═══════════════════════════════════════════════════════════════

    [Benchmark(Description = "MultiplierOverageCost: within quota")]
    public decimal OverageCost_WithinQuota()
    {
        var plan = new PlanData
        {
            UseMultiplierBilling = true,
            MonthlyRequestQuota = 1000m,
            OverageRatePerRequest = 0.01m
        };
        return _calculator.CalculateMultiplierOverageCost(1.0m, 500m, plan);
    }

    [Benchmark(Description = "MultiplierOverageCost: over quota")]
    public decimal OverageCost_OverQuota()
    {
        var plan = new PlanData
        {
            UseMultiplierBilling = true,
            MonthlyRequestQuota = 1000m,
            OverageRatePerRequest = 0.01m
        };
        return _calculator.CalculateMultiplierOverageCost(1.0m, 1050m, plan);
    }
}
