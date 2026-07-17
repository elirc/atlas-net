namespace Atlas.Domain.Entities;

/// <summary>
/// A country in which Atlas can employ workers on behalf of clients.
/// Carries the currency and statutory cost rates used by payroll.
/// </summary>
public class Country
{
    /// <summary>ISO 3166-1 alpha-2 code, e.g. "PH". Primary key.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>ISO 4217 currency code, e.g. "PHP".</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>
    /// Statutory employer cost on top of gross salary (social security, mandatory
    /// benefits) expressed as a fraction, e.g. 0.12 for 12%.
    /// </summary>
    public required decimal EmployerCostRate { get; set; }

    /// <summary>
    /// Flat employee-side deduction (income tax withholding + contributions)
    /// expressed as a fraction of gross, e.g. 0.20 for 20%. Simplified model.
    /// </summary>
    public required decimal EmployeeDeductionRate { get; set; }

    /// <summary>
    /// Minimum days of notice between giving notice and the last day of
    /// employment for the standard termination flow.
    /// </summary>
    public int MinimumNoticeDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;
}
