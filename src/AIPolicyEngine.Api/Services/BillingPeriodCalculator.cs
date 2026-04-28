namespace AIPolicyEngine.Api.Services;

/// <summary>
/// Calculates billing period boundaries based on a configurable cycle start day.
/// </summary>
public static class BillingPeriodCalculator
{
    public static DateTime GetCurrentPeriodStartUtc(DateTime nowUtc, int cycleStartDay)
    {
        var normalizedStartDay = NormalizeCycleStartDay(cycleStartDay);
        var utcNow = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();

        var currentMonthDay = Math.Min(normalizedStartDay, DateTime.DaysInMonth(utcNow.Year, utcNow.Month));
        var currentMonthStart = new DateTime(utcNow.Year, utcNow.Month, currentMonthDay, 0, 0, 0, DateTimeKind.Utc);
        if (utcNow >= currentMonthStart)
            return currentMonthStart;

        var previousMonth = utcNow.AddMonths(-1);
        var previousMonthDay = Math.Min(normalizedStartDay, DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month));
        return new DateTime(previousMonth.Year, previousMonth.Month, previousMonthDay, 0, 0, 0, DateTimeKind.Utc);
    }

    public static int NormalizeCycleStartDay(int cycleStartDay) => Math.Clamp(cycleStartDay, 1, 31);
}
