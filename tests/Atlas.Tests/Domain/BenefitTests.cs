using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class BenefitTests
{
    private static BenefitPlan Plan(decimal cost, decimal employerRate) => new()
    {
        CountryCode = "PH",
        Name = "HealthGuard Plus",
        MonthlyCost = cost,
        EmployerContributionRate = employerRate,
    };

    private static BenefitEnrollment Enrollment(string start, string? end = null)
    {
        var enrollment = new BenefitEnrollment
        {
            ContractId = Guid.NewGuid(),
            BenefitPlanId = Guid.NewGuid(),
            StartDate = DateOnly.Parse(start),
        };
        if (end is not null)
        {
            enrollment.End(DateOnly.Parse(end), DateTimeOffset.UtcNow);
        }
        return enrollment;
    }

    [Theory]
    [InlineData("4500", "0.80", "3600.00", "900.00")]
    [InlineData("999.99", "0.80", "799.99", "200.00")]  // rounded employer share, remainder to employee
    [InlineData("100", "0", "0.00", "100.00")]           // employee pays everything
    [InlineData("100", "1", "100.00", "0.00")]           // employer pays everything
    [InlineData("33.33", "0.5", "16.67", "16.66")]       // 16.665 midpoint: away from zero
    public void Shares_SplitPremium_WithoutLosingACent(string cost, string rate, string employer, string employee)
    {
        var plan = Plan(decimal.Parse(cost), decimal.Parse(rate));

        Assert.Equal(decimal.Parse(employer), plan.EmployerShare);
        Assert.Equal(decimal.Parse(employee), plan.EmployeeShare);
        Assert.Equal(plan.MonthlyCost, plan.EmployerShare + plan.EmployeeShare);
    }

    [Theory]
    [InlineData("2026-02-01", null, 2026, 1, false)]  // starts after January
    [InlineData("2026-02-01", null, 2026, 2, true)]
    [InlineData("2026-02-15", null, 2026, 2, true)]   // mid-month start covers the month
    [InlineData("2026-02-01", "2026-03-10", 2026, 3, true)]  // ends mid-March: still covered
    [InlineData("2026-02-01", "2026-03-10", 2026, 4, false)] // ended before April
    public void CoversMonth_MatchesOverlapOfCoverageAndMonth(
        string start, string? end, int year, int month, bool expected)
    {
        var enrollment = Enrollment(start, end);

        Assert.Equal(expected, enrollment.CoversMonth(year, month));
    }

    [Fact]
    public void End_BeforeStart_Throws()
    {
        var enrollment = Enrollment("2026-02-01");

        Assert.Throws<DomainException>(() =>
            enrollment.End(new DateOnly(2026, 1, 15), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void End_Twice_Throws()
    {
        var enrollment = Enrollment("2026-02-01", "2026-06-30");

        Assert.Throws<DomainException>(() =>
            enrollment.End(new DateOnly(2026, 7, 31), DateTimeOffset.UtcNow));
    }
}
