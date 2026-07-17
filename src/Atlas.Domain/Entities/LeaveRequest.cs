namespace Atlas.Domain.Entities;

public enum LeaveRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>
/// A worker's request to take leave under an employment contract.
/// Lifecycle: Pending -> Approved | Rejected (decided by the client) or
/// Pending -> Cancelled (withdrawn). Pending and Approved requests reserve
/// days against the year's balance; Rejected and Cancelled release them.
/// </summary>
public class LeaveRequest : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Optimistic-concurrency token; bumped on every update.</summary>
    public int Version { get; set; }

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    public required LeaveType Type { get; set; }

    /// <summary>First calendar day of leave (inclusive).</summary>
    public required DateOnly StartDate { get; set; }

    /// <summary>Last calendar day of leave (inclusive). Same calendar year as StartDate.</summary>
    public required DateOnly EndDate { get; set; }

    /// <summary>Working days (Mon-Fri) the request consumes, computed at submission.</summary>
    public required int Days { get; set; }

    public string? Reason { get; set; }

    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>Approver's note; required when rejecting.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>True while the request reserves days against the balance.</summary>
    public bool CountsAgainstBalance => Status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved;

    /// <summary>True when the request's date range intersects [start, end].</summary>
    public bool Overlaps(DateOnly start, DateOnly end) => StartDate <= end && EndDate >= start;

    public void Approve(DateTimeOffset nowUtc, string? note = null)
    {
        if (Status != LeaveRequestStatus.Pending)
        {
            throw new DomainException($"Only pending leave requests can be approved; this request is {Status}.");
        }

        Status = LeaveRequestStatus.Approved;
        DecidedAtUtc = nowUtc;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Reject(string note, DateTimeOffset nowUtc)
    {
        if (Status != LeaveRequestStatus.Pending)
        {
            throw new DomainException($"Only pending leave requests can be rejected; this request is {Status}.");
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("A rejection note is required.");
        }

        Status = LeaveRequestStatus.Rejected;
        DecidedAtUtc = nowUtc;
        DecisionNote = note.Trim();
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        if (Status != LeaveRequestStatus.Pending)
        {
            throw new DomainException($"Only pending leave requests can be cancelled; this request is {Status}.");
        }

        Status = LeaveRequestStatus.Cancelled;
        DecidedAtUtc = nowUtc;
    }
}
