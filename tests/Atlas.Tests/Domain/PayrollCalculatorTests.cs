using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

public class PayrollCalculatorTests
{
    private static Country CountryWith(decimal employerRate, decimal deductionRate) => new()
    {
        Code = "XX",
        Name = "Testland",
        CurrencyCode = "XTS",
        EmployerCostRate = employerRate,
        EmployeeDeductionRate = deductionRate,
    };

    [Fact]
    public void Calculate_RoundNumbers_ProducesExpectedBreakdown()
    {
        var country = CountryWith(employerRate: 0.12m, deductionRate: 0.15m);

        var amounts = PayrollCalculator.Calculate(100_000m, country);

        Assert.Equal(100_000m, amounts.Gross);
        Assert.Equal(12_000m, amounts.EmployerCost);
        Assert.Equal(15_000m, amounts.EmployeeDeductions);
        Assert.Equal(85_000m, amounts.NetPay);
        Assert.Equal(112_000m, amounts.TotalCost);
    }

    [Fact]
    public void Calculate_AwkwardRates_RoundsEachComponentToTwoDecimals()
    {
        var country = CountryWith(employerRate: 0.138m, deductionRate: 0.25m);

        var amounts = PayrollCalculator.Calculate(3_333.33m, country);

        Assert.Equal(3_333.33m, amounts.Gross);
        Assert.Equal(460.00m, amounts.EmployerCost); // 3333.33 * 0.138 = 459.99954 -> 460.00
        Assert.Equal(833.33m, amounts.EmployeeDeductions); // 833.3325 -> 833.33
        Assert.Equal(2_500.00m, amounts.NetPay);
        Assert.Equal(3_793.33m, amounts.TotalCost);
    }

    [Fact]
    public void Calculate_MidpointAmounts_RoundAwayFromZero()
    {
        var country = CountryWith(employerRate: 0.5m, deductionRate: 0.5m);

        var amounts = PayrollCalculator.Calculate(0.05m, country);

        // 0.05 * 0.5 = 0.025 -> away from zero -> 0.03
        Assert.Equal(0.03m, amounts.EmployerCost);
        Assert.Equal(0.03m, amounts.EmployeeDeductions);
        Assert.Equal(0.02m, amounts.NetPay);
    }

    [Fact]
    public void Calculate_NetPlusDeductions_AlwaysEqualsGross()
    {
        var country = CountryWith(employerRate: 0.2137m, deductionRate: 0.3319m);

        foreach (var gross in new[] { 1234.56m, 99.99m, 100_000.01m, 7_777.77m })
        {
            var amounts = PayrollCalculator.Calculate(gross, country);

            Assert.Equal(amounts.Gross, amounts.NetPay + amounts.EmployeeDeductions);
            Assert.Equal(amounts.Gross + amounts.EmployerCost, amounts.TotalCost);
        }
    }

    [Fact]
    public void Calculate_ZeroRates_NetEqualsGrossAndNoEmployerCost()
    {
        var country = CountryWith(employerRate: 0m, deductionRate: 0m);

        var amounts = PayrollCalculator.Calculate(5_000m, country);

        Assert.Equal(5_000m, amounts.NetPay);
        Assert.Equal(0m, amounts.EmployerCost);
        Assert.Equal(5_000m, amounts.TotalCost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Calculate_NonPositiveGross_Throws(double gross)
    {
        var country = CountryWith(0.1m, 0.1m);

        Assert.Throws<DomainException>(() => PayrollCalculator.Calculate((decimal)gross, country));
    }

    [Fact]
    public void RoundMoney_UsesAwayFromZeroMidpointRounding()
    {
        Assert.Equal(1.13m, PayrollCalculator.RoundMoney(1.125m));
        Assert.Equal(-1.13m, PayrollCalculator.RoundMoney(-1.125m));
        Assert.Equal(1.12m, PayrollCalculator.RoundMoney(1.124m));
    }
}

public class PayrollRunLifecycleTests
{
    [Fact]
    public void Complete_DraftRun_SetsStatusAndTimestamp()
    {
        var run = new PayrollRun { CountryCode = "PH", Year = 2026, Month = 7 };
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        run.Complete(now);

        Assert.Equal(PayrollRunStatus.Completed, run.Status);
        Assert.Equal(now, run.CompletedAtUtc);
    }

    [Fact]
    public void Complete_Twice_Throws()
    {
        var run = new PayrollRun { CountryCode = "PH", Year = 2026, Month = 7 };
        run.Complete(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => run.Complete(DateTimeOffset.UtcNow));
    }
}
