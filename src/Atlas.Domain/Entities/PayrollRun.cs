namespace Atlas.Domain.Entities;

public enum PayrollRunStatus
{
    Draft = 0,
    Completed = 1,
}

/// <summary>
/// A payroll cycle for one country and one calendar month. Created as Draft with
/// computed payslips; completing it is one-way and triggers client invoicing.
/// </summary>
public class PayrollRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string CountryCode { get; set; }
    public Country? Country { get; set; }

    public required int Year { get; set; }

    /// <summary>Calendar month, 1-12.</summary>
    public required int Month { get; set; }

    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public List<Payslip> Payslips { get; set; } = [];

    public void Complete(DateTimeOffset nowUtc)
    {
        if (Status != PayrollRunStatus.Draft)
        {
            throw new DomainException($"Only draft payroll runs can be completed; this run is {Status}.");
        }

        Status = PayrollRunStatus.Completed;
        CompletedAtUtc = nowUtc;
    }
}
