using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class EmploymentContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static EmploymentContract NewDraft(DateOnly? startDate = null) => new()
    {
        ClientId = Guid.NewGuid(),
        WorkerId = Guid.NewGuid(),
        CountryCode = "PH",
        JobTitle = "Engineer",
        MonthlySalary = 100_000m,
        CurrencyCode = "PHP",
        StartDate = startDate ?? new DateOnly(2026, 2, 1),
    };

    [Fact]
    public void Activate_FromDraft_BecomesActiveAndStampsTime()
    {
        var contract = NewDraft();

        contract.Activate(Now);

        Assert.Equal(ContractStatus.Active, contract.Status);
        Assert.Equal(Now, contract.ActivatedAtUtc);
    }

    [Fact]
    public void Activate_WhenAlreadyActive_Throws()
    {
        var contract = NewDraft();
        contract.Activate(Now);

        var ex = Assert.Throws<DomainException>(() => contract.Activate(Now));
        Assert.Contains("Only draft contracts", ex.Message);
    }

    [Fact]
    public void Activate_WhenTerminated_Throws()
    {
        var contract = NewDraft();
        contract.Activate(Now);
        contract.Terminate(new DateOnly(2026, 6, 30), "End of project", Now);

        Assert.Throws<DomainException>(() => contract.Activate(Now));
    }

    [Fact]
    public void Terminate_ActiveContract_SetsEndDateReasonAndTime()
    {
        var contract = NewDraft();
        contract.Activate(Now);

        contract.Terminate(new DateOnly(2026, 9, 30), "  Redundancy  ", Now);

        Assert.Equal(ContractStatus.Terminated, contract.Status);
        Assert.Equal(new DateOnly(2026, 9, 30), contract.EndDate);
        Assert.Equal("Redundancy", contract.TerminationReason);
        Assert.Equal(Now, contract.TerminatedAtUtc);
    }

    [Fact]
    public void Terminate_DraftContract_Throws()
    {
        var contract = NewDraft();

        var ex = Assert.Throws<DomainException>(
            () => contract.Terminate(new DateOnly(2026, 9, 30), "reason", Now));
        Assert.Contains("Only active contracts", ex.Message);
    }

    [Fact]
    public void Terminate_Twice_Throws()
    {
        var contract = NewDraft();
        contract.Activate(Now);
        contract.Terminate(new DateOnly(2026, 9, 30), "reason", Now);

        Assert.Throws<DomainException>(
            () => contract.Terminate(new DateOnly(2026, 10, 31), "again", Now));
    }

    [Fact]
    public void Terminate_EndDateBeforeStartDate_Throws()
    {
        var contract = NewDraft(startDate: new DateOnly(2026, 5, 1));
        contract.Activate(Now);

        var ex = Assert.Throws<DomainException>(
            () => contract.Terminate(new DateOnly(2026, 4, 30), "reason", Now));
        Assert.Contains("cannot be before the start date", ex.Message);
    }

    [Fact]
    public void Terminate_OnStartDate_IsAllowed()
    {
        var contract = NewDraft(startDate: new DateOnly(2026, 5, 1));
        contract.Activate(Now);

        contract.Terminate(new DateOnly(2026, 5, 1), "no-show", Now);

        Assert.Equal(ContractStatus.Terminated, contract.Status);
    }

    [Fact]
    public void Terminate_BlankReason_Throws()
    {
        var contract = NewDraft();
        contract.Activate(Now);

        Assert.Throws<DomainException>(
            () => contract.Terminate(new DateOnly(2026, 9, 30), "   ", Now));
    }

    [Theory]
    [InlineData(2026, 1, false)] // before start month
    [InlineData(2026, 2, true)]  // starts mid-scope: month of start date
    [InlineData(2026, 5, true)]  // fully inside
    [InlineData(2026, 9, true)]  // month of end date
    [InlineData(2026, 10, false)] // after end date
    public void CoversMonth_ChecksOverlapWithEmploymentPeriod(int year, int month, bool expected)
    {
        var contract = NewDraft(startDate: new DateOnly(2026, 2, 10));
        contract.Activate(Now);
        contract.Terminate(new DateOnly(2026, 9, 15), "end", Now);

        Assert.Equal(expected, contract.CoversMonth(year, month));
    }

    [Fact]
    public void CoversMonth_DraftContract_IsNeverCovered()
    {
        var contract = NewDraft(startDate: new DateOnly(2026, 1, 1));

        Assert.False(contract.CoversMonth(2026, 3));
    }

    [Fact]
    public void CoversMonth_ActiveContractWithNoEndDate_CoversMonthsAfterStart()
    {
        var contract = NewDraft(startDate: new DateOnly(2026, 2, 1));
        contract.Activate(Now);

        Assert.True(contract.CoversMonth(2026, 2));
        Assert.True(contract.CoversMonth(2027, 12));
        Assert.False(contract.CoversMonth(2026, 1));
    }
}
