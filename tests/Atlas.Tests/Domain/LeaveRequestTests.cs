using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class LeaveRequestTests
{
    private static LeaveRequest NewRequest() => new()
    {
        ContractId = Guid.NewGuid(),
        Type = LeaveType.Annual,
        StartDate = new DateOnly(2026, 8, 3),
        EndDate = new DateOnly(2026, 8, 7),
        Days = 5,
    };

    [Fact]
    public void Approve_Pending_BecomesApproved()
    {
        var request = NewRequest();
        var now = DateTimeOffset.UtcNow;

        request.Approve(now, "Enjoy!");

        Assert.Equal(LeaveRequestStatus.Approved, request.Status);
        Assert.Equal(now, request.DecidedAtUtc);
        Assert.Equal("Enjoy!", request.DecisionNote);
        Assert.True(request.CountsAgainstBalance); // approved still reserves days
    }

    [Fact]
    public void Approve_AlreadyApproved_Throws()
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
    public void Reject_Pending_BecomesRejected_AndReleasesBalance()
    {
        var request = NewRequest();

        request.Reject("Busy period", DateTimeOffset.UtcNow);

        Assert.Equal(LeaveRequestStatus.Rejected, request.Status);
        Assert.Equal("Busy period", request.DecisionNote);
        Assert.False(request.CountsAgainstBalance);
    }

    [Fact]
    public void Cancel_Approved_Throws()
    {
        var request = NewRequest();
        request.Approve(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => request.Cancel(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("2026-08-07", "2026-08-10", true)]  // touches the last day
    [InlineData("2026-08-01", "2026-08-03", true)]  // touches the first day
    [InlineData("2026-08-04", "2026-08-05", true)]  // fully inside
    [InlineData("2026-08-10", "2026-08-11", false)] // after
    [InlineData("2026-07-30", "2026-08-02", false)] // before
    public void Overlaps_DetectsIntersectingRanges(string start, string end, bool expected)
    {
        var request = NewRequest(); // 2026-08-03 .. 2026-08-07

        Assert.Equal(expected, request.Overlaps(DateOnly.Parse(start), DateOnly.Parse(end)));
    }
}
