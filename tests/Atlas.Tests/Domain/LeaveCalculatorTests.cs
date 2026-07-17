using Atlas.Domain;
using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

public class LeaveCalculatorTests
{
    // 2026-08-03 is a Monday.
    [Theory]
    [InlineData("2026-08-03", "2026-08-03", 1)] // single weekday
    [InlineData("2026-08-03", "2026-08-07", 5)] // Monday..Friday
    [InlineData("2026-08-03", "2026-08-09", 5)] // full week including weekend
    [InlineData("2026-08-01", "2026-08-02", 0)] // Saturday..Sunday only
    [InlineData("2026-08-07", "2026-08-10", 2)] // Friday..Monday across a weekend
    [InlineData("2026-08-03", "2026-08-14", 10)] // two working weeks
    public void CountWorkingDays_CountsMondayToFridayInclusive(string start, string end, int expected)
    {
        var days = LeaveCalculator.CountWorkingDays(DateOnly.Parse(start), DateOnly.Parse(end));

        Assert.Equal(expected, days);
    }

    [Fact]
    public void CountWorkingDays_EndBeforeStart_Throws()
    {
        Assert.Throws<DomainException>(() =>
            LeaveCalculator.CountWorkingDays(new DateOnly(2026, 8, 7), new DateOnly(2026, 8, 3)));
    }
}
