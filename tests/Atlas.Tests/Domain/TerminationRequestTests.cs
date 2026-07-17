using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class TerminationRequestTests
{
    private static TerminationRequest NewRequest() => new()
    {
        ContractId = Guid.NewGuid(),
        Reason = "Role eliminated",
        NoticeDate = new DateOnly(2026, 7, 1),
        ProposedEndDate = new DateOnly(2026, 8, 15),
    };

    [Fact]
    public void EarliestAllowedEndDate_AddsNoticeDays()
    {
        var earliest = TerminationRequest.EarliestAllowedEndDate(new DateOnly(2026, 7, 1), 30);

        Assert.Equal(new DateOnly(2026, 7, 31), earliest);
    }

    [Fact]
    public void Approve_Pending_BecomesApproved()
    {
        var request = NewRequest();

        request.Approve(DateTimeOffset.UtcNow, "Confirmed with client");

        Assert.Equal(TerminationRequestStatus.Approved, request.Status);
        Assert.Equal("Confirmed with client", request.DecisionNote);
        Assert.NotNull(request.DecidedAtUtc);
    }

    [Fact]
    public void Approve_Twice_Throws()
    {
        var request = NewRequest();
        request.Approve(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => request.Approve(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Reject_RequiresNote()
    {
        var request = NewRequest();

        Assert.Throws<DomainException>(() => request.Reject("  ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_Decided_Throws()
    {
        var request = NewRequest();
        request.Reject("Retained after counter-offer", DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => request.Cancel(DateTimeOffset.UtcNow));
    }
}
