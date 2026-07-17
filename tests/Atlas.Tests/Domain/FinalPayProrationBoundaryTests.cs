using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

/// <summary>
/// Boundary cases for final-month proration: month-length edges (28/29/30/31
/// days), leap years, and first/last-day terminations.
/// </summary>
public class FinalPayProrationBoundaryTests
{
    [Theory]
    [InlineData(2026, 2, 28)] // February, non-leap year
    [InlineData(2028, 2, 29)] // February, leap year
    [InlineData(2026, 4, 30)] // 30-day month
    [InlineData(2026, 7, 31)] // 31-day month
    public void ProrateFinalMonth_EndOnLastDayOfMonth_PaysFullSalary(int year, int month, int day)
    {
        var prorated = FinalPayCalculator.ProrateFinalMonth(6_000m, new DateOnly(year, month, day));

        Assert.Equal(6_000m, prorated);
    }

    [Fact]
    public void ProrateFinalMonth_EndOnFirstDayOfFebruary_PaysOneTwentyEighth()
    {
        // 6000 * 1/28 = 214.2857 -> 214.29 (away from zero).
        var prorated = FinalPayCalculator.ProrateFinalMonth(6_000m, new DateOnly(2026, 2, 1));

        Assert.Equal(214.29m, prorated);
    }

    [Fact]
    public void ProrateFinalMonth_LeapYearFebruary_UsesTwentyNineDays()
    {
        // 6000 * 15/29 = 3103.448... -> 3103.45; a 28-day divisor would give 3214.29.
        var prorated = FinalPayCalculator.ProrateFinalMonth(6_000m, new DateOnly(2028, 2, 15));

        Assert.Equal(3_103.45m, prorated);
    }

    [Fact]
    public void ProrateFinalMonth_NonLeapFebruary_UsesTwentyEightDays()
    {
        // 6000 * 15/28 = 3214.2857 -> 3214.29.
        var prorated = FinalPayCalculator.ProrateFinalMonth(6_000m, new DateOnly(2027, 2, 15));

        Assert.Equal(3_214.29m, prorated);
    }

    [Theory]
    [InlineData(2026, 1, 1)]  // 6000 * 1/31 = 193.548 -> 193.55
    [InlineData(2026, 4, 1)]  // 6000 * 1/30 = 200.00
    public void ProrateFinalMonth_EndOnFirstDayOfMonth_PaysOneDay(int year, int month, int day)
    {
        var expected = PayrollCalculator.RoundMoney(6_000m / DateTime.DaysInMonth(year, month));

        var prorated = FinalPayCalculator.ProrateFinalMonth(6_000m, new DateOnly(year, month, day));

        Assert.Equal(expected, prorated);
    }

    [Fact]
    public void ProrateFinalMonth_NeverExceedsMonthlySalary_ForAnyDayOfALeapYear()
    {
        // Property-style sweep: an awkward salary prorated on every day of a leap
        // year never exceeds the full salary and grows monotonically inside a month.
        var salary = 5_432.19m;
        for (var day = new DateOnly(2028, 1, 1); day.Year == 2028; day = day.AddDays(1))
        {
            var prorated = FinalPayCalculator.ProrateFinalMonth(salary, day);

            Assert.InRange(prorated, 0.01m, salary);
            if (day.Day > 1)
            {
                var previous = FinalPayCalculator.ProrateFinalMonth(salary, day.AddDays(-1));
                Assert.True(prorated >= previous, $"Proration decreased from {previous} to {prorated} at {day}.");
            }
        }
    }

    [Fact]
    public void UnusedLeavePayout_ZeroDays_IsZero()
    {
        Assert.Equal(0m, FinalPayCalculator.UnusedLeavePayout(6_000m, 0));
    }

    [Fact]
    public void UnusedLeavePayout_RoundsTheDailyRateOnce_NotPerDay()
    {
        // 8666.67 * 12 / 260 = 400.0002 -> daily rate 400.00; ten days must pay
        // exactly 4000.00 (the rounded rate times days, no per-day drift).
        var dailyRate = FinalPayCalculator.DailyRate(8_666.67m);
        Assert.Equal(400.00m, dailyRate);

        Assert.Equal(4_000.00m, FinalPayCalculator.UnusedLeavePayout(8_666.67m, 10));
    }

    [Fact]
    public void UnusedLeavePayout_TinySalary_RoundsDailyRateToZero()
    {
        // 0.01 * 12 / 260 = 0.00046 -> 0.00 per day, so any number of days pays 0.
        Assert.Equal(0m, FinalPayCalculator.DailyRate(0.01m));
        Assert.Equal(0m, FinalPayCalculator.UnusedLeavePayout(0.01m, 30));
    }

    [Fact]
    public void UnusedLeavePayout_EqualsRoundedDailyRateTimesDays()
    {
        foreach (var salary in new[] { 1_000m, 3_333.33m, 12_345.67m, 99.99m })
        {
            foreach (var days in new[] { 1, 5, 15, 25 })
            {
                var expected = PayrollCalculator.RoundMoney(FinalPayCalculator.DailyRate(salary) * days);

                Assert.Equal(expected, FinalPayCalculator.UnusedLeavePayout(salary, days));
            }
        }
    }
}
