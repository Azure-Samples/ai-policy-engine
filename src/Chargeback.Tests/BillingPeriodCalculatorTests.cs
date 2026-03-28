using Chargeback.Api.Services;

namespace Chargeback.Tests;

public class BillingPeriodCalculatorTests
{
    [Fact]
    public void GetCurrentPeriodStartUtc_DefaultCycleStart_ReturnsFirstOfMonth()
    {
        var now = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        var periodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(now, 1);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), periodStart);
    }

    [Fact]
    public void GetCurrentPeriodStartUtc_CustomCycleStart_AfterStartDay_UsesCurrentMonth()
    {
        var now = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        var periodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(now, 15);
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), periodStart);
    }

    [Fact]
    public void GetCurrentPeriodStartUtc_CustomCycleStart_BeforeStartDay_UsesPreviousMonth()
    {
        var now = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);
        var periodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(now, 15);
        Assert.Equal(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc), periodStart);
    }

    [Fact]
    public void GetCurrentPeriodStartUtc_HandlesMonthWithFewerDays()
    {
        var now = new DateTime(2026, 2, 10, 10, 0, 0, DateTimeKind.Utc);
        var periodStart = BillingPeriodCalculator.GetCurrentPeriodStartUtc(now, 31);
        Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), periodStart);
    }
}
