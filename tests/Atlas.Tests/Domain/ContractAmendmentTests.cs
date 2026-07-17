using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class ContractAmendmentTests
{
    private static ContractAmendment NewAmendment() => new()
    {
        ContractId = Guid.NewGuid(),
        NewMonthlySalary = 150000m,
        NewJobTitle = "Staff Engineer",
        EffectiveDate = new DateOnly(2026, 9, 1),
    };

    private static EmploymentContract ActiveContract()
    {
        var contract = new EmploymentContract
        {
            ClientId = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(),
            CountryCode = "PH",
            JobTitle = "Engineer",
            MonthlySalary = 100000m,
            CurrencyCode = "PHP",
            StartDate = new DateOnly(2026, 1, 1),
        };
        contract.Activate(DateTimeOffset.UtcNow);
        return contract;
    }

    [Fact]
    public void Approve_Pending_BecomesApproved()
    {
        var amendment = NewAmendment();

        amendment.Approve(DateTimeOffset.UtcNow, "Well deserved");

        Assert.Equal(AmendmentStatus.Approved, amendment.Status);
        Assert.Equal("Well deserved", amendment.DecisionNote);
        Assert.NotNull(amendment.DecidedAtUtc);
    }

    [Fact]
    public void Approve_Twice_Throws()
    {
        var amendment = NewAmendment();
        amendment.Approve(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => amendment.Approve(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Reject_RequiresNote()
    {
        var amendment = NewAmendment();

        Assert.Throws<DomainException>(() => amendment.Reject("", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_Decided_Throws()
    {
        var amendment = NewAmendment();
        amendment.Reject("Not now", DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => amendment.Cancel(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ApplyAmendment_UpdatesSalaryAndTitle()
    {
        var contract = ActiveContract();

        contract.ApplyAmendment(150000m, "Staff Engineer");

        Assert.Equal(150000m, contract.MonthlySalary);
        Assert.Equal("Staff Engineer", contract.JobTitle);
    }

    [Fact]
    public void ApplyAmendment_TitleOnly_LeavesSalaryUntouched()
    {
        var contract = ActiveContract();

        contract.ApplyAmendment(null, "Principal Engineer");

        Assert.Equal(100000m, contract.MonthlySalary);
        Assert.Equal("Principal Engineer", contract.JobTitle);
    }

    [Fact]
    public void ApplyAmendment_OnDraftContract_Throws()
    {
        var contract = new EmploymentContract
        {
            ClientId = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(),
            CountryCode = "PH",
            JobTitle = "Engineer",
            MonthlySalary = 100000m,
            CurrencyCode = "PHP",
            StartDate = new DateOnly(2026, 1, 1),
        };

        Assert.Throws<DomainException>(() => contract.ApplyAmendment(150000m, null));
    }

    [Fact]
    public void ApplyAmendment_NoChanges_Throws()
    {
        var contract = ActiveContract();

        Assert.Throws<DomainException>(() => contract.ApplyAmendment(null, "  "));
    }

    [Fact]
    public void ApplyAmendment_NonPositiveSalary_Throws()
    {
        var contract = ActiveContract();

        Assert.Throws<DomainException>(() => contract.ApplyAmendment(0m, null));
    }
}
