using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class ExpenseClaimTests
{
    private static ExpenseClaim NewClaim()
    {
        var claim = new ExpenseClaim
        {
            ContractId = Guid.NewGuid(),
            CurrencyCode = "PHP",
        };
        claim.Items.Add(new ExpenseItem
        {
            ExpenseClaimId = claim.Id,
            Description = "Taxi to client site",
            Amount = 350.50m,
            IncurredDate = new DateOnly(2026, 7, 1),
        });
        claim.Items.Add(new ExpenseItem
        {
            ExpenseClaimId = claim.Id,
            Description = "Team lunch",
            Amount = 1200m,
            IncurredDate = new DateOnly(2026, 7, 2),
        });
        return claim;
    }

    [Fact]
    public void TotalAmount_SumsAllItems()
    {
        var claim = NewClaim();

        Assert.Equal(1550.50m, claim.TotalAmount);
    }

    [Fact]
    public void Approve_Pending_BecomesApproved()
    {
        var claim = NewClaim();
        var now = DateTimeOffset.UtcNow;

        claim.Approve(now, "Looks right");

        Assert.Equal(ExpenseClaimStatus.Approved, claim.Status);
        Assert.Equal(now, claim.DecidedAtUtc);
        Assert.Equal("Looks right", claim.DecisionNote);
    }

    [Fact]
    public void Reject_RequiresNote()
    {
        var claim = NewClaim();

        Assert.Throws<DomainException>(() => claim.Reject(" ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkReimbursed_Pending_Throws()
    {
        var claim = NewClaim();

        Assert.Throws<DomainException>(() => claim.MarkReimbursed(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkReimbursed_Approved_RecordsRunAndTimestamp()
    {
        var claim = NewClaim();
        claim.Approve(DateTimeOffset.UtcNow);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        claim.MarkReimbursed(runId, now);

        Assert.Equal(ExpenseClaimStatus.Reimbursed, claim.Status);
        Assert.Equal(runId, claim.ReimbursedInPayrollRunId);
        Assert.Equal(now, claim.ReimbursedAtUtc);
    }

    [Fact]
    public void MarkReimbursed_Twice_Throws()
    {
        var claim = NewClaim();
        claim.Approve(DateTimeOffset.UtcNow);
        claim.MarkReimbursed(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => claim.MarkReimbursed(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}
