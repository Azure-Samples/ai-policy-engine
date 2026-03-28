using BenchmarkDotNet.Attributes;
using Chargeback.Api.Models;
using Chargeback.Api.Services;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CalculatorBenchmarks
{
    private readonly ChargebackCalculator _calculator = new();
    private CachedLogData _logData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            Model = "gpt-4o",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700,
            ImageTokens = 0
        };
    }

    [Benchmark]
    public decimal CalculateCost() => _calculator.CalculateCost(_logData);

    [Benchmark]
    public decimal CalculateCustomerCost()
    {
        var plan = new PlanData { CostPerMillionTokens = 15m };
        _logData.IsOverbilled = true;
        return _calculator.CalculateCustomerCost(_logData, plan);
    }
}
