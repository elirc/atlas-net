namespace Atlas.Domain.Entities;

public enum AmendmentStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>
/// A request to change an active contract's terms (salary and/or job title)
/// from an effective date. Lifecycle: Pending -> Approved | Rejected | Cancelled.
/// Approval applies the change to the contract and appends an immutable
/// <see cref="SalaryRecord"/>; a contract can hold only one pending amendment.
/// </summary>
public class ContractAmendment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    /// <summary>New gross monthly salary; null when only the title changes.</summary>
    public decimal? NewMonthlySalary { get; set; }

    /// <summary>New job title; null when only the salary changes.</summary>
    public string? NewJobTitle { get; set; }

    /// <summary>First calendar day the amended terms apply.</summary>
    public required DateOnly EffectiveDate { get; set; }

    public string? Reason { get; set; }

    public AmendmentStatus Status { get; set; } = AmendmentStatus.Pending;

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>Approver's note; required when rejecting.</summary>
    public string? DecisionNote { get; set; }

    public void Approve(DateTimeOffset nowUtc, string? note = null)
    {
        if (Status != AmendmentStatus.Pending)
        {
            throw new DomainException($"Only pending amendments can be approved; this amendment is {Status}.");
        }

        Status = AmendmentStatus.Approved;
        DecidedAtUtc = nowUtc;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Reject(string note, DateTimeOffset nowUtc)
    {
        if (Status != AmendmentStatus.Pending)
        {
            throw new DomainException($"Only pending amendments can be rejected; this amendment is {Status}.");
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("A rejection note is required.");
        }

        Status = AmendmentStatus.Rejected;
        DecidedAtUtc = nowUtc;
        DecisionNote = note.Trim();
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        if (Status != AmendmentStatus.Pending)
        {
            throw new DomainException($"Only pending amendments can be cancelled; this amendment is {Status}.");
        }

        Status = AmendmentStatus.Cancelled;
        DecidedAtUtc = nowUtc;
    }
}
