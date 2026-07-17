namespace Atlas.Domain.Entities;

public enum BenefitEnrollmentStatus
{
    Active = 0,
    Ended = 1,
}

/// <summary>
/// A contract's enrollment in a benefit plan from a start date. Payroll charges
/// the plan's premium for every month the enrollment covers (full month when it
/// covers any part, mirroring contract coverage). Ending an enrollment is one-way.
/// </summary>
public class BenefitEnrollment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    public required Guid BenefitPlanId { get; set; }
    public BenefitPlan? BenefitPlan { get; set; }

    /// <summary>First calendar day of coverage.</summary>
    public required DateOnly StartDate { get; set; }

    /// <summary>Last calendar day of coverage, set when the enrollment ends.</summary>
    public DateOnly? EndDate { get; set; }

    public BenefitEnrollmentStatus Status { get; set; } = BenefitEnrollmentStatus.Active;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }

    /// <summary>True when coverage overlaps any part of the calendar month.</summary>
    public bool CoversMonth(int year, int month)
    {
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return StartDate <= monthEnd && (EndDate is null || EndDate.Value >= monthStart);
    }

    /// <summary>Ends coverage on <paramref name="endDate"/> (inclusive).</summary>
    public void End(DateOnly endDate, DateTimeOffset nowUtc)
    {
        if (Status != BenefitEnrollmentStatus.Active)
        {
            throw new DomainException($"Only active enrollments can be ended; this enrollment is {Status}.");
        }
        if (endDate < StartDate)
        {
            throw new DomainException(
                $"End date {endDate:yyyy-MM-dd} cannot be before the enrollment start date {StartDate:yyyy-MM-dd}.");
        }

        Status = BenefitEnrollmentStatus.Ended;
        EndDate = endDate;
        EndedAtUtc = nowUtc;
    }
}
