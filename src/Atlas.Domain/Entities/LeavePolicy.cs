namespace Atlas.Domain.Entities;

public enum LeaveType
{
    Annual = 0,
    Sick = 1,
}

/// <summary>
/// The statutory leave allowances Atlas grants in one country: how many working
/// days of annual and sick leave a worker accrues per calendar year.
/// </summary>
public class LeavePolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Country the policy applies to (ISO 3166-1 alpha-2). One policy per country.</summary>
    public required string CountryCode { get; set; }
    public Country? Country { get; set; }

    /// <summary>Annual (vacation) leave allowance in working days per calendar year.</summary>
    public required int AnnualLeaveDays { get; set; }

    /// <summary>Sick leave allowance in working days per calendar year.</summary>
    public required int SickLeaveDays { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int AllowanceFor(LeaveType type) => type switch
    {
        LeaveType.Annual => AnnualLeaveDays,
        LeaveType.Sick => SickLeaveDays,
        _ => throw new DomainException($"Unknown leave type '{type}'."),
    };
}
