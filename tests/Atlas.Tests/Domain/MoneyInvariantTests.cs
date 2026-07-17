using Atlas.Domain.Entities;
using Atlas.Domain.Services;

namespace Atlas.Tests.Domain;

/// <summary>
/// Property-style sweeps over the money math: whatever the inputs, cents are
/// conserved, components stay at 2 decimal places, and rounding never lets a
/// split drift away from its total.
/// </summary>
public class MoneyInvariantTests
{
    private static readonly decimal[] SampleAmounts =
        [0.01m, 0.99m, 1m, 33.33m, 99.99m, 1_234.56m, 6_789.01m, 54_321.09m, 1_000_000.01m];

    private static readonly decimal[] SampleRates =
        [0m, 0.001m, 0.07m, 0.1m, 0.125m, 0.3333m, 0.5m, 0.6667m, 0.9m, 1m];

    private static bool HasAtMostTwoDecimals(decimal value) =>
        value == Math.Round(value, 2);

    [Fact]
    public void PayrollCalculator_ConservesCents_AcrossGrossAndRateGrid()
    {
        foreach (var gross in SampleAmounts)
        {
            foreach (var rate in SampleRates)
            {
                var country = new Country
                {
                    Code = "XX",
                    Name = "Testland",
                    CurrencyCode = "XTS",
                    EmployerCostRate = rate,
                    EmployeeDeductionRate = rate,
                };

                var amounts = PayrollCalculator.Calculate(gross, country);

                // Cent conservation: the worker's split and the client's bill re-add exactly.
                Assert.Equal(amounts.Gross, amounts.NetPay + amounts.EmployeeDeductions);
                Assert.Equal(amounts.Gross + amounts.EmployerCost, amounts.TotalCost);

                // Every component is a real money amount at 2 decimal places.
                Assert.True(HasAtMostTwoDecimals(amounts.Gross), $"Gross {amounts.Gross} has sub-cent precision.");
                Assert.True(HasAtMostTwoDecimals(amounts.EmployerCost), $"EmployerCost {amounts.EmployerCost} has sub-cent precision.");
                Assert.True(HasAtMostTwoDecimals(amounts.EmployeeDeductions), $"Deductions {amounts.EmployeeDeductions} has sub-cent precision.");
                Assert.True(HasAtMostTwoDecimals(amounts.NetPay), $"NetPay {amounts.NetPay} has sub-cent precision.");

                Assert.True(amounts.EmployerCost >= 0);
                Assert.True(amounts.EmployeeDeductions >= 0);
            }
        }
    }

    [Fact]
    public void BenefitPlan_ShareSplit_NeverGainsOrLosesACent()
    {
        foreach (var premium in SampleAmounts)
        {
            foreach (var rate in SampleRates)
            {
                var plan = new BenefitPlan
                {
                    CountryCode = "XX",
                    Name = "Sweep plan",
                    MonthlyCost = premium,
                    EmployerContributionRate = rate,
                };

                // The employee share is defined as the remainder, so the split must
                // re-add to the premium exactly — rounding may shift a cent between
                // the parties but never create or destroy one.
                Assert.Equal(premium, plan.EmployerShare + plan.EmployeeShare);
                Assert.InRange(plan.EmployerShare, 0m, premium);
                Assert.InRange(plan.EmployeeShare, 0m, premium);
                Assert.True(HasAtMostTwoDecimals(plan.EmployerShare));
                Assert.True(HasAtMostTwoDecimals(plan.EmployeeShare));
            }
        }
    }

    [Fact]
    public void BenefitPlan_FullEmployerRate_LeavesEmployeeShareZero()
    {
        var plan = new BenefitPlan
        {
            CountryCode = "XX",
            Name = "All employer",
            MonthlyCost = 250.75m,
            EmployerContributionRate = 1m,
        };

        Assert.Equal(250.75m, plan.EmployerShare);
        Assert.Equal(0m, plan.EmployeeShare);
    }

    [Fact]
    public void BenefitPlan_HalfCentSplit_EmployerRoundsUp_EmployeeAbsorbsTheDifference()
    {
        // 0.01 * 0.5 = 0.005 -> employer 0.01 (away from zero), employee 0.00.
        var plan = new BenefitPlan
        {
            CountryCode = "XX",
            Name = "Penny plan",
            MonthlyCost = 0.01m,
            EmployerContributionRate = 0.5m,
        };

        Assert.Equal(0.01m, plan.EmployerShare);
        Assert.Equal(0.00m, plan.EmployeeShare);
    }

    [Fact]
    public void FxConversion_RoundsOnceOnTheTotal_WithinHalfACent()
    {
        // Invoicing converts the already-rounded invoice total once; the result
        // must sit within half a cent of the exact product for any awkward rate.
        decimal[] rates = [0.017321m, 0.008547m, 1.234567m, 56.789012m];
        foreach (var total in SampleAmounts)
        {
            foreach (var rate in rates)
            {
                var converted = PayrollCalculator.RoundMoney(total * rate);

                Assert.True(HasAtMostTwoDecimals(converted));
                Assert.True(Math.Abs(converted - total * rate) < 0.005m,
                    $"Converting {total} at {rate} drifted more than half a cent.");
            }
        }
    }

    [Fact]
    public void FxConversion_SummingRoundedPerInvoiceTotals_CanDifferFromRoundingTheSum()
    {
        // Documents why the code rounds per invoice (one client each) and never
        // re-derives a grand total by converting a sum: the two disagree by design.
        var rate = 0.0175m;
        decimal[] invoiceTotals = [100.10m, 100.10m, 100.10m];

        var perInvoice = invoiceTotals.Sum(t => PayrollCalculator.RoundMoney(t * rate));
        var onSum = PayrollCalculator.RoundMoney(invoiceTotals.Sum() * rate);

        Assert.Equal(5.25m, perInvoice); // 3 x (1.75175 -> 1.75)
        Assert.Equal(5.26m, onSum);      // 300.30 * 0.0175 = 5.25525 -> 5.26
        Assert.NotEqual(perInvoice, onSum);
    }
}
