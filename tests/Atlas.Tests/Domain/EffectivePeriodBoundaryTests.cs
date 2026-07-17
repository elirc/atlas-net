using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

/// <summary>
/// Exact-boundary behavior of the period-selection rules payroll depends on:
/// salary records and FX rates effective precisely on a month's first/last day,
/// and month-coverage checks for contracts and benefit enrollments whose start
/// or end lands exactly on a month edge.
/// </summary>
public class EffectivePeriodBoundaryTests
{
    private static SalaryRecord Record(decimal salary, DateOnly effective, DateTimeOffset? createdAt = null) => new()
    {
        ContractId = Guid.NewGuid(),
        MonthlySalary = salary,
        JobTitle = "Engineer",
        EffectiveDate = effective,
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void SalaryRecord_EffectiveExactlyOnMonthStart_AppliesToThatMonth()
    {
        var records = new[]
        {
            Record(1_000m, new DateOnly(2026, 1, 1)),
            Record(2_000m, new DateOnly(2026, 3, 1)),
        };

        Assert.Equal(2_000m, SalaryRecord.EffectiveForMonth(records, 2026, 3)!.MonthlySalary);
        Assert.Equal(1_000m, SalaryRecord.EffectiveForMonth(records, 2026, 2)!.MonthlySalary);
    }

    [Fact]
    public void SalaryRecord_EffectiveExactlyOnMonthEnd_AppliesToThatWholeMonth()
    {
        var records = new[]
        {
            Record(1_000m, new DateOnly(2026, 1, 1)),
            Record(2_000m, new DateOnly(2026, 3, 31)),
        };

        // Mid-month rule: a change effective on the month's last day still owns March.
        Assert.Equal(2_000m, SalaryRecord.EffectiveForMonth(records, 2026, 3)!.MonthlySalary);
    }

    [Fact]
    public void SalaryRecord_EffectiveFirstDayOfNextMonth_DoesNotLeakIntoPriorMonth()
    {
        var records = new[]
        {
            Record(1_000m, new DateOnly(2026, 1, 1)),
            Record(2_000m, new DateOnly(2026, 4, 1)),
        };

        Assert.Equal(1_000m, SalaryRecord.EffectiveForMonth(records, 2026, 3)!.MonthlySalary);
        Assert.Equal(2_000m, SalaryRecord.EffectiveForMonth(records, 2026, 4)!.MonthlySalary);
    }

    [Fact]
    public void SalaryRecord_LeapFebruary_MonthEndIsThe29th()
    {
        var records = new[]
        {
            Record(1_000m, new DateOnly(2028, 1, 1)),
            Record(2_000m, new DateOnly(2028, 2, 29)),
        };

        Assert.Equal(2_000m, SalaryRecord.EffectiveForMonth(records, 2028, 2)!.MonthlySalary);
    }

    private static FxRate Rate(decimal rate, DateOnly effective, DateTimeOffset? createdAt = null) => new()
    {
        BaseCurrencyCode = "PHP",
        QuoteCurrencyCode = "USD",
        Rate = rate,
        EffectiveDate = effective,
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void FxRate_EffectiveExactlyOnMonthEnd_AppliesToThatMonth()
    {
        var rates = new[]
        {
            Rate(0.017m, new DateOnly(2026, 1, 1)),
            Rate(0.018m, new DateOnly(2026, 3, 31)),
        };

        Assert.Equal(0.018m, FxRate.EffectiveForMonth(rates, 2026, 3)!.Rate);
        Assert.Equal(0.017m, FxRate.EffectiveForMonth(rates, 2026, 2)!.Rate);
    }

    [Fact]
    public void FxRate_EffectiveFirstDayOfNextMonth_DoesNotApplyToPriorMonth()
    {
        var rates = new[] { Rate(0.018m, new DateOnly(2026, 4, 1)) };

        Assert.Null(FxRate.EffectiveForMonth(rates, 2026, 3));
        Assert.Equal(0.018m, FxRate.EffectiveForMonth(rates, 2026, 4)!.Rate);
    }

    private static EmploymentContract Contract(DateOnly start, DateOnly? end = null) => new()
    {
        ClientId = Guid.NewGuid(),
        WorkerId = Guid.NewGuid(),
        CountryCode = "PH",
        JobTitle = "Engineer",
        MonthlySalary = 1_000m,
        CurrencyCode = "PHP",
        StartDate = start,
        EndDate = end,
        Status = ContractStatus.Active,
    };

    [Fact]
    public void ContractCoversMonth_StartOnLastDayOfMonth_IsCovered()
    {
        var contract = Contract(new DateOnly(2026, 3, 31));

        Assert.True(contract.CoversMonth(2026, 3));
        Assert.False(contract.CoversMonth(2026, 2));
    }

    [Fact]
    public void ContractCoversMonth_StartOnFirstDayOfNextMonth_IsNotCovered()
    {
        var contract = Contract(new DateOnly(2026, 4, 1));

        Assert.False(contract.CoversMonth(2026, 3));
        Assert.True(contract.CoversMonth(2026, 4));
    }

    [Fact]
    public void ContractCoversMonth_EndOnFirstDayOfMonth_IsStillCovered()
    {
        var contract = Contract(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1));

        Assert.True(contract.CoversMonth(2026, 3));
        Assert.False(contract.CoversMonth(2026, 4));
    }

    [Fact]
    public void ContractCoversMonth_EndOnLastDayOfPreviousMonth_IsNotCovered()
    {
        var contract = Contract(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28));

        Assert.True(contract.CoversMonth(2026, 2));
        Assert.False(contract.CoversMonth(2026, 3));
    }

    private static BenefitEnrollment Enrollment(DateOnly start, DateOnly? end = null) => new()
    {
        ContractId = Guid.NewGuid(),
        BenefitPlanId = Guid.NewGuid(),
        StartDate = start,
        EndDate = end,
    };

    [Fact]
    public void EnrollmentCoversMonth_StartOnLastDayOfMonth_ChargesThatMonth()
    {
        var enrollment = Enrollment(new DateOnly(2026, 5, 31));

        Assert.True(enrollment.CoversMonth(2026, 5));
        Assert.False(enrollment.CoversMonth(2026, 4));
    }

    [Fact]
    public void EnrollmentCoversMonth_EndOnFirstDayOfMonth_StillChargesThatMonth()
    {
        var enrollment = Enrollment(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1));

        Assert.True(enrollment.CoversMonth(2026, 6));
        Assert.False(enrollment.CoversMonth(2026, 7));
    }
}
