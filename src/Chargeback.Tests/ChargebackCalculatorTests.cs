using Chargeback.Api.Models;
using Chargeback.Api.Services;

namespace Chargeback.Tests;

public class ChargebackCalculatorTests
{
    private readonly ChargebackCalculator _calculator = new();

    [Theory]
    [InlineData("gpt-4.1", 1000, 500, 0.06)]       // 1K * 0.02 + 0.5K * 0.08
    [InlineData("gpt-4", 2000, 1000, 0.09)]         // 2K * 0.02 + 1K * 0.05
    [InlineData("gpt-4.1-mini", 10000, 5000, 0.12)] // 10K * 0.004 + 5K * 0.016
    [InlineData("gpt-4o-mini", 1000, 1000, 0.02)]   // 1K * 0.005 + 1K * 0.015
    public void CalculateCost_KnownModel_ReturnsCorrectCost(string model, long prompt, long completion, decimal expected)
    {
        var data = new CachedLogData
        {
            DeploymentId = model,
            PromptTokens = prompt,
            CompletionTokens = completion
        };

        var cost = _calculator.CalculateCost(data);

        Assert.Equal(expected, cost);
    }

    [Fact]
    public void CalculateCost_UnknownModel_ReturnsZero()
    {
        var data = new CachedLogData
        {
            DeploymentId = "unknown-model-v99",
            PromptTokens = 5000,
            CompletionTokens = 2000
        };

        Assert.Equal(0m, _calculator.CalculateCost(data));
    }

    [Fact]
    public void CalculateCost_CaseInsensitive()
    {
        var data = new CachedLogData
        {
            DeploymentId = "GPT-4O",
            PromptTokens = 1000,
            CompletionTokens = 500
        };

        var cost = _calculator.CalculateCost(data);

        Assert.Equal(0.06m, cost);
    }

    [Fact]
    public void CalculateCost_FallsBackToModel_WhenDeploymentIdUnknown()
    {
        var data = new CachedLogData
        {
            DeploymentId = "my-custom-deployment",
            Model = "gpt-4o",
            PromptTokens = 1000,
            CompletionTokens = 500
        };

        Assert.Equal(0.06m, _calculator.CalculateCost(data));
    }

    [Fact]
    public void CalculateCost_ZeroTokens_ReturnsZero()
    {
        var data = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            PromptTokens = 0,
            CompletionTokens = 0
        };

        Assert.Equal(0m, _calculator.CalculateCost(data));
    }

    [Fact]
    public void CalculateCost_EmbeddingModel()
    {
        var data = new CachedLogData
        {
            DeploymentId = "text-embedding-3-large",
            PromptTokens = 8000,
            CompletionTokens = 0
        };

        // 8K * 0.001 = 0.008
        Assert.Equal(0.008m, _calculator.CalculateCost(data));
    }

    [Fact]
    public void CalculateCustomerCost_Overbilled_WithPositiveCost_ReturnsCalculatedCost()
    {
        var logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            TotalTokens = 500_000,
            IsOverbilled = true
        };
        var plan = new PlanData { CostPerMillionTokens = 10m };

        var cost = _calculator.CalculateCustomerCost(logData, plan);

        // 500_000 / 1_000_000 * 10 = 5.0
        Assert.Equal(5.0m, cost);
    }

    [Fact]
    public void CalculateCustomerCost_NotOverbilled_ReturnsZero()
    {
        var logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            TotalTokens = 500_000,
            IsOverbilled = false
        };
        var plan = new PlanData { CostPerMillionTokens = 10m };

        Assert.Equal(0m, _calculator.CalculateCustomerCost(logData, plan));
    }

    [Fact]
    public void CalculateCustomerCost_Overbilled_ZeroCostPerMillion_ReturnsZero()
    {
        var logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            TotalTokens = 500_000,
            IsOverbilled = true
        };
        var plan = new PlanData { CostPerMillionTokens = 0m };

        Assert.Equal(0m, _calculator.CalculateCustomerCost(logData, plan));
    }

    [Fact]
    public void CalculateCustomerCost_LargeTokenCount_ReturnsPreciseResult()
    {
        var logData = new CachedLogData
        {
            DeploymentId = "gpt-4o",
            TotalTokens = 123_456_789,
            IsOverbilled = true
        };
        var plan = new PlanData { CostPerMillionTokens = 3.50m };

        var cost = _calculator.CalculateCustomerCost(logData, plan);

        // 123_456_789 / 1_000_000 * 3.50 = 432.098761...  rounded to 4 decimal places
        Assert.Equal(432.0988m, cost);
    }
}
