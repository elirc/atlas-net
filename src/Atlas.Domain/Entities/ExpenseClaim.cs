namespace Atlas.Domain.Entities;

public enum ExpenseClaimStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Reimbursed = 3,
}

/// <summary>
/// A worker's claim for out-of-pocket expenses under an employment contract.
/// Lifecycle: Pending -> Approved | Rejected; Approved -> Reimbursed when the
/// next payroll run for the contract's country picks the claim up and pays it
/// out with the worker's net pay (and bills it on to the client).
/// </summary>
public class ExpenseClaim : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Optimistic-concurrency token; bumped on every update.</summary>
    public int Version { get; set; }

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    /// <summary>ISO 4217 currency of all items; always the contract's local currency.</summary>
    public required string CurrencyCode { get; set; }

    public string? Description { get; set; }

    public ExpenseClaimStatus Status { get; set; } = ExpenseClaimStatus.Pending;

    public DateTimeOffset SubmittedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>Approver's note; required when rejecting.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>The payroll run that reimbursed this claim, set when Status becomes Reimbursed.</summary>
    public Guid? ReimbursedInPayrollRunId { get; set; }
    public DateTimeOffset? ReimbursedAtUtc { get; set; }

    public List<ExpenseItem> Items { get; set; } = [];

    /// <summary>Sum of all line items. Requires Items to be loaded.</summary>
    public decimal TotalAmount => Items.Sum(i => i.Amount);

    public void Approve(DateTimeOffset nowUtc, string? note = null)
    {
        if (Status != ExpenseClaimStatus.Pending)
        {
            throw new DomainException($"Only pending expense claims can be approved; this claim is {Status}.");
        }

        Status = ExpenseClaimStatus.Approved;
        DecidedAtUtc = nowUtc;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Reject(string note, DateTimeOffset nowUtc)
    {
        if (Status != ExpenseClaimStatus.Pending)
        {
            throw new DomainException($"Only pending expense claims can be rejected; this claim is {Status}.");
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("A rejection note is required.");
        }

        Status = ExpenseClaimStatus.Rejected;
        DecidedAtUtc = nowUtc;
        DecisionNote = note.Trim();
    }

    /// <summary>Called by payroll when the claim is paid out in a run.</summary>
    public void MarkReimbursed(Guid payrollRunId, DateTimeOffset nowUtc)
    {
        if (Status != ExpenseClaimStatus.Approved)
        {
            throw new DomainException($"Only approved expense claims can be reimbursed; this claim is {Status}.");
        }

        Status = ExpenseClaimStatus.Reimbursed;
        ReimbursedInPayrollRunId = payrollRunId;
        ReimbursedAtUtc = nowUtc;
    }
}

/// <summary>One line on an expense claim, optionally linking a receipt image/PDF by URL.</summary>
public class ExpenseItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ExpenseClaimId { get; set; }
    public ExpenseClaim? ExpenseClaim { get; set; }

    public required string Description { get; set; }

    /// <summary>Amount in the claim's currency; must be positive.</summary>
    public required decimal Amount { get; set; }

    /// <summary>Calendar day the expense was incurred.</summary>
    public required DateOnly IncurredDate { get; set; }

    /// <summary>Absolute http(s) URL of the uploaded receipt, if any.</summary>
    public string? ReceiptUrl { get; set; }
}
