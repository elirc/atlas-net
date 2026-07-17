using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class SalaryRecordTests
{
    private static SalaryRecord Record(decimal salary, string effectiveDate, DateTimeOffset? createdAt = null) => new()
    {
        ContractId = Guid.Empty,
        MonthlySalary = salary,
        JobTitle = "Engineer",
        EffectiveDate = DateOnly.Parse(effectiveDate),
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public void EffectiveForMonth_PicksLatestRecordOnOrBeforeMonthEnd()
    {
        var records = new[]
        {
            Record(100000m, "2026-01-01"),
            Record(120000m, "2026-08-01"),
        };

        Assert.Equal(100000m, SalaryRecord.EffectiveForMonth(records, 2026, 7)!.MonthlySalary);
        Assert.Equal(120000m, SalaryRecord.EffectiveForMonth(records, 2026, 8)!.MonthlySalary);
        Assert.Equal(120000m, SalaryRecord.EffectiveForMonth(records, 2026, 12)!.MonthlySalary);
    }

    [Fact]
    public void EffectiveForMonth_MidMonthChange_AppliesToThatWholeMonth()
    {
        var records = new[]
        {
            Record(100000m, "2026-01-01"),
            Record(130000m, "2026-08-15"),
        };

        Assert.Equal(130000m, SalaryRecord.EffectiveForMonth(records, 2026, 8)!.MonthlySalary);
        Assert.Equal(100000m, SalaryRecord.EffectiveForMonth(records, 2026, 7)!.MonthlySalary);
    }

    [Fact]
    public void EffectiveForMonth_NoRecordYet_ReturnsNull()
    {
        var records = new[] { Record(100000m, "2026-06-01") };

        Assert.Null(SalaryRecord.EffectiveForMonth(records, 2026, 5));
    }

    [Fact]
    public void EffectiveForMonth_SameEffectiveDate_BreaksTieByCreationTime()
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;
        var records = new[]
        {
            Record(110000m, "2026-08-01", earlier),
            Record(125000m, "2026-08-01", later),
        };

        Assert.Equal(125000m, SalaryRecord.EffectiveForMonth(records, 2026, 8)!.MonthlySalary);
    }
}
