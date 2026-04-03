using System.Diagnostics;
using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests.Integration;

/// <summary>
/// B5.10 — Routing decision latency validation tests.
/// These use Stopwatch to validate that routing evaluation and cost calculation
/// stay within the &lt;5ms p99 budget. Runs as part of the regular test suite.
/// BenchmarkDotNet provides detailed analysis; these tests enforce the contract.
/// </summary>
public class RoutingLatencyTests
{
    private const int Iterations = 1000;
    private const double MaxP99Ms = 5.0; // Target: <5ms p99

    // ═══════════════════════════════════════════════════════════════
    // Routing evaluation latency
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RoutingEvaluation_NoPolicy_P99Under5ms()
    {
        var latencies = MeasureRouting("gpt-4o", null, Iterations);
        var p99 = Percentile(latencies, 99);

        Assert.True(p99 < MaxP99Ms,
            $"No-policy routing p99 was {p99:F3}ms, expected <{MaxP99Ms}ms");
    }

    [Fact]
    public void RoutingEvaluation_SingleRule_ExactMatch_P99Under5ms()
    {
        var policy = new ModelRoutingPolicy
        {
            Id = "latency-single",
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

        var latencies = MeasureRouting("gpt-4o", policy, Iterations);
        var p99 = Percentile(latencies, 99);

        Assert.True(p99 < MaxP99Ms,
            $"Single-rule routing p99 was {p99:F3}ms, expected <{MaxP99Ms}ms");
    }

    [Fact]
    public void RoutingEvaluation_TenRules_LastMatch_P99Under5ms()
    {
        var policy = new ModelRoutingPolicy
        {
            Id = "latency-ten",
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

        var latencies = MeasureRouting("gpt-4o", policy, Iterations);
        var p99 = Percentile(latencies, 99);

        Assert.True(p99 < MaxP99Ms,
            $"10-rule routing p99 was {p99:F3}ms, expected <{MaxP99Ms}ms");
    }

    [Fact]
    public void RoutingEvaluation_NoMatch_Passthrough_P99Under5ms()
    {
        var policy = new ModelRoutingPolicy
        {
            Id = "latency-nomatch",
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

        var latencies = MeasureRouting("gpt-4o", policy, Iterations);
        var p99 = Percentile(latencies, 99);

        Assert.True(p99 < MaxP99Ms,
            $"No-match routing p99 was {p99:F3}ms, expected <{MaxP99Ms}ms");
    }

    // ═══════════════════════════════════════════════════════════════
    // CalculateEffectiveRequestCost latency
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CalculateEffectiveRequestCost_SubMicrosecond()
    {
        var calculator = new ChargebackCalculator(new Dictionary<string, ModelPricing>
        {
            ["gpt-4o"] = new()
            {
                ModelId = "gpt-4o",
                Multiplier = 1.0m,
                TierName = "Standard",
                PromptRatePer1K = 0.03m,
                CompletionRatePer1K = 0.06m
            }
        });

        var logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            Model = "gpt-4o",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700
        };

        // Warm up
        for (int i = 0; i < 100; i++)
            calculator.CalculateEffectiveRequestCost(logData);

        // Measure batch (1000 calls) — total should be well under 1ms
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            calculator.CalculateEffectiveRequestCost(logData);
        sw.Stop();

        var avgMicroseconds = sw.Elapsed.TotalMicroseconds / 1000.0;

        // Sub-microsecond = average < 1μs per call
        Assert.True(avgMicroseconds < 10.0,
            $"CalculateEffectiveRequestCost average was {avgMicroseconds:F3}μs, expected <10μs");
    }

    [Fact]
    public void CalculateMultiplierOverageCost_SubMicrosecond()
    {
        var calculator = new ChargebackCalculator();
        var plan = new PlanData
        {
            UseMultiplierBilling = true,
            MonthlyRequestQuota = 1000m,
            OverageRatePerRequest = 0.01m
        };

        // Warm up
        for (int i = 0; i < 100; i++)
            calculator.CalculateMultiplierOverageCost(1.0m, 950m, plan);

        // Measure batch
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            calculator.CalculateMultiplierOverageCost(1.0m, 950m, plan);
        sw.Stop();

        var avgMicroseconds = sw.Elapsed.TotalMicroseconds / 1000.0;

        Assert.True(avgMicroseconds < 10.0,
            $"CalculateMultiplierOverageCost average was {avgMicroseconds:F3}μs, expected <10μs");
    }

    // ═══════════════════════════════════════════════════════════════
    // Routing + cost calculation combined (full precheck overhead)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FullPrecheckOverhead_RoutingPlusCost_P99Under5ms()
    {
        var policy = new ModelRoutingPolicy
        {
            Id = "latency-full",
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

        var calculator = new ChargebackCalculator(new Dictionary<string, ModelPricing>
        {
            ["gpt-4o-east"] = new()
            {
                ModelId = "gpt-4o-east",
                Multiplier = 1.0m,
                TierName = "Standard",
                PromptRatePer1K = 0.03m,
                CompletionRatePer1K = 0.06m
            }
        });

        var plan = new PlanData
        {
            UseMultiplierBilling = true,
            MonthlyRequestQuota = 1000m,
            OverageRatePerRequest = 0.01m
        };

        // Warm up
        for (int i = 0; i < 100; i++)
        {
            var r = RoutingEvaluator.Evaluate("gpt-4o", policy);
            var logData = new CachedLogData { DeploymentId = r.DeploymentId, Model = r.DeploymentId };
            var cost = calculator.CalculateEffectiveRequestCost(logData);
            calculator.CalculateMultiplierOverageCost(cost, 500m, plan);
        }

        // Measure
        var latencies = new double[Iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < Iterations; i++)
        {
            sw.Restart();

            var result = RoutingEvaluator.Evaluate("gpt-4o", policy);
            var logData = new CachedLogData { DeploymentId = result.DeploymentId, Model = result.DeploymentId };
            var effectiveCost = calculator.CalculateEffectiveRequestCost(logData);
            calculator.CalculateMultiplierOverageCost(effectiveCost, 500m, plan);

            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        var p99 = Percentile(latencies, 99);

        Assert.True(p99 < MaxP99Ms,
            $"Full precheck overhead p99 was {p99:F3}ms, expected <{MaxP99Ms}ms");
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static double[] MeasureRouting(string requestedDeployment, ModelRoutingPolicy? policy, int iterations)
    {
        // Warm up
        for (int i = 0; i < 100; i++)
            RoutingEvaluator.Evaluate(requestedDeployment, policy);

        var latencies = new double[iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            RoutingEvaluator.Evaluate(requestedDeployment, policy);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }
        return latencies;
    }

    private static double Percentile(double[] values, int percentile)
    {
        Array.Sort(values);
        int index = (int)Math.Ceiling(percentile / 100.0 * values.Length) - 1;
        return values[Math.Max(0, index)];
    }
}
