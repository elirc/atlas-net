using Atlas.Domain.Services;

namespace Atlas.Domain.Entities;

/// <summary>
/// A benefit package Atlas offers in one country (health, pension, ...), priced
/// as a monthly premium in the country's currency and split between employer
/// and employee by a contribution rate. The employer share is billed to the
/// client; the employee share is deducted from the worker's net pay.
/// </summary>
public class BenefitPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Country the plan is offered in (ISO 3166-1 alpha-2).</summary>
    public required string CountryCode { get; set; }
    public Country? Country { get; set; }

    /// <summary>Plan name, unique within the country, e.g. "Health Plus".</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Total monthly premium in the country's currency; positive.</summary>
    public required decimal MonthlyCost { get; set; }

    /// <summary>
    /// Fraction of the premium the employer pays, in [0, 1]. The employee pays
    /// the remainder via a payroll deduction.
    /// </summary>
    public required decimal EmployerContributionRate { get; set; }

    /// <summary>Inactive plans accept no new enrollments; existing ones keep running.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The employer's monthly share, rounded to 2 dp away from zero.</summary>
    public decimal EmployerShare => PayrollCalculator.RoundMoney(MonthlyCost * EmployerContributionRate);

    /// <summary>The employee's monthly share: premium minus employer share (no rounding drift).</summary>
    public decimal EmployeeShare => MonthlyCost - EmployerShare;
}
