using Atlas.Domain;
using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

public class FinalPayCalculatorTests
{
    [Theory]
    [InlineData("100000", "2026-07-15", "48387.10")] // 15 of 31 days
    [InlineData("100000", "2026-07-31", "100000.00")] // last day: full month
    [InlineData("100000", "2026-02-14", "50000.00")]  // 14 of 28 days
    [InlineData("100000", "2026-07-01", "3225.81")]   // single day
    public void ProrateFinalMonth_ProratesByCalendarDays(string salary, string endDate, string expected)
    {
        var prorated = FinalPayCalculator.ProrateFinalMonth(decimal.Parse(salary), DateOnly.Parse(endDate));

        Assert.Equal(decimal.Parse(expected), prorated);
    }

    [Fact]
    public void ProrateFinalMonth_NonPositiveSalary_Throws()
    {
        Assert.Throws<DomainException>(() =>
            FinalPayCalculator.ProrateFinalMonth(0m, new DateOnly(2026, 7, 15)));
    }

    [Fact]
    public void DailyRate_SpreadsTwelveSalariesOver260WorkingDays()
    {
        Assert.Equal(4615.38m, FinalPayCalculator.DailyRate(100000m));
    }

    [Theory]
    [InlineData(10, "46153.80")]
    [InlineData(0, "0.00")]
    [InlineData(1, "4615.38")]
    public void UnusedLeavePayout_PaysDailyRatePerDay(int days, string expected)
    {
        Assert.Equal(decimal.Parse(expected), FinalPayCalculator.UnusedLeavePayout(100000m, days));
    }

    [Fact]
    public void UnusedLeavePayout_NegativeDays_Throws()
    {
        Assert.Throws<DomainException>(() => FinalPayCalculator.UnusedLeavePayout(100000m, -1));
    }
}
