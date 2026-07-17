namespace Atlas.Domain.Entities;

public enum TerminationRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>
/// The standard off-boarding flow: a request to terminate an active contract on
/// a proposed end date that satisfies the country's minimum notice period,
/// counted from the day notice is given (the request date). Approving executes
/// the termination; the final payroll run pays prorated salary plus an
/// unused-leave payout. Lifecycle: Pending -> Approved | Rejected | Cancelled.
/// (Immediate termination via the contract endpoint remains for cause.)
/// </summary>
public class TerminationRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    public required string Reason { get; set; }

    /// <summary>Day notice was given (the request date, UTC).</summary>
    public required DateOnly NoticeDate { get; set; }

    /// <summary>Proposed last day of employment (inclusive).</summary>
    public required DateOnly ProposedEndDate { get; set; }

    public TerminationRequestStatus Status { get; set; } = TerminationRequestStatus.Pending;

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>Decider's note; required when rejecting.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>The earliest end date the notice period allows.</summary>
    public static DateOnly EarliestAllowedEndDate(DateOnly noticeDate, int minimumNoticeDays) =>
        noticeDate.AddDays(minimumNoticeDays);

    public void Approve(DateTimeOffset nowUtc, string? note = null)
    {
        if (Status != TerminationRequestStatus.Pending)
        {
            throw new DomainException($"Only pending termination requests can be approved; this request is {Status}.");
        }

        Status = TerminationRequestStatus.Approved;
        DecidedAtUtc = nowUtc;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Reject(string note, DateTimeOffset nowUtc)
    {
        if (Status != TerminationRequestStatus.Pending)
        {
            throw new DomainException($"Only pending termination requests can be rejected; this request is {Status}.");
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("A rejection note is required.");
        }

        Status = TerminationRequestStatus.Rejected;
        DecidedAtUtc = nowUtc;
        DecisionNote = note.Trim();
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        if (Status != TerminationRequestStatus.Pending)
        {
            throw new DomainException($"Only pending termination requests can be cancelled; this request is {Status}.");
        }

        Status = TerminationRequestStatus.Cancelled;
        DecidedAtUtc = nowUtc;
    }
}
