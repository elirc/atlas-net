using Atlas.Domain.Entities;
using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

public class FxRateTests
{
    private static FxRate Rate(decimal rate, string effectiveDate, DateTimeOffset? createdAt = null) => new()
    {
        BaseCurrencyCode = "PHP",
        QuoteCurrencyCode = "USD",
        Rate = rate,
        EffectiveDate = DateOnly.Parse(effectiveDate),
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void EffectiveForMonth_PicksLatestRateOnOrBeforeMonthEnd()
    {
        var rates = new[]
        {
            Rate(0.0170m, "2026-01-01"),
            Rate(0.0180m, "2026-08-01"),
        };

        Assert.Equal(0.0170m, FxRate.EffectiveForMonth(rates, 2026, 7)!.Rate);
        Assert.Equal(0.0180m, FxRate.EffectiveForMonth(rates, 2026, 8)!.Rate);
        Assert.Equal(0.0180m, FxRate.EffectiveForMonth(rates, 2026, 12)!.Rate);
    }

    [Fact]
    public void EffectiveForMonth_MidMonthRate_AppliesToThatMonth()
    {
        var rates = new[]
        {
            Rate(0.0170m, "2026-01-01"),
            Rate(0.0185m, "2026-08-15"),
        };

        Assert.Equal(0.0185m, FxRate.EffectiveForMonth(rates, 2026, 8)!.Rate);
    }

    [Fact]
    public void EffectiveForMonth_NoRateYet_ReturnsNull()
    {
        var rates = new[] { Rate(0.0170m, "2026-06-01") };

        Assert.Null(FxRate.EffectiveForMonth(rates, 2026, 5));
    }

    [Fact]
    public void EffectiveForMonth_SameEffectiveDate_BreaksTieByCreationTime()
    {
        var rates = new[]
        {
            Rate(0.0170m, "2026-08-01", DateTimeOffset.UtcNow.AddMinutes(-5)),
            Rate(0.0175m, "2026-08-01", DateTimeOffset.UtcNow),
        };

        Assert.Equal(0.0175m, FxRate.EffectiveForMonth(rates, 2026, 8)!.Rate);
    }

    // Conversion rounding: 2 dp, midpoints away from zero, mirroring payroll money math.
    [Theory]
    [InlineData("113550.50", "0.0171", "1941.71")]  // 1941.71355 rounds down
    [InlineData("23450", "0.0001", "2.35")]          // 2.345 is a midpoint: away from zero
    [InlineData("100000", "0.0171", "1710.00")]      // exact
    [InlineData("123456.78", "0.92", "113580.24")]   // 113580.2376 rounds down
    [InlineData("50", "0.0001", "0.01")]             // 0.005 midpoint: away from zero
    public void Conversion_RoundsToTwoDecimalsAwayFromZero(string amount, string rate, string expected)
    {
        var converted = PayrollCalculator.RoundMoney(decimal.Parse(amount) * decimal.Parse(rate));

        Assert.Equal(decimal.Parse(expected), converted);
    }
}
