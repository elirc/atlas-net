namespace Atlas.Domain.Entities;

public enum ContractStatus
{
    Draft = 0,
    Active = 1,
    Terminated = 2,
}

/// <summary>
/// The employment relationship Atlas holds with a worker on behalf of a client.
/// Lifecycle: Draft -> Active -> Terminated (one-way).
/// </summary>
public class EmploymentContract
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public required Guid WorkerId { get; set; }
    public Worker? Worker { get; set; }

    /// <summary>Country of employment (ISO 3166-1 alpha-2).</summary>
    public required string CountryCode { get; set; }
    public Country? Country { get; set; }

    public required string JobTitle { get; set; }

    /// <summary>Gross monthly salary in <see cref="CurrencyCode"/>.</summary>
    public required decimal MonthlySalary { get; set; }

    /// <summary>ISO 4217 code; always the currency of the country of employment.</summary>
    public required string CurrencyCode { get; set; }

    public required DateOnly StartDate { get; set; }

    /// <summary>Last day of employment, set on termination.</summary>
    public DateOnly? EndDate { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? TerminatedAtUtc { get; set; }
    public string? TerminationReason { get; set; }

    /// <summary>Moves a draft contract into the Active state.</summary>
    public void Activate(DateTimeOffset nowUtc)
    {
        if (Status != ContractStatus.Draft)
        {
            throw new DomainException($"Only draft contracts can be activated; this contract is {Status}.");
        }

        Status = ContractStatus.Active;
        ActivatedAtUtc = nowUtc;
    }

    /// <summary>Terminates an active contract effective <paramref name="endDate"/>.</summary>
    public void Terminate(DateOnly endDate, string reason, DateTimeOffset nowUtc)
    {
        if (Status != ContractStatus.Active)
        {
            throw new DomainException($"Only active contracts can be terminated; this contract is {Status}.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A termination reason is required.");
        }
        if (endDate < StartDate)
        {
            throw new DomainException($"End date {endDate:yyyy-MM-dd} cannot be before the start date {StartDate:yyyy-MM-dd}.");
        }

        Status = ContractStatus.Terminated;
        EndDate = endDate;
        TerminationReason = reason.Trim();
        TerminatedAtUtc = nowUtc;
    }

    /// <summary>
    /// True when the contract has been activated and its employment period overlaps
    /// the given calendar month. Used by payroll to select contracts to pay.
    /// </summary>
    public bool CoversMonth(int year, int month)
    {
        if (Status == ContractStatus.Draft)
        {
            return false;
        }

        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        return StartDate <= monthEnd && (EndDate is null || EndDate.Value >= monthStart);
    }
}
