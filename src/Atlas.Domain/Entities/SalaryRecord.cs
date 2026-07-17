namespace Atlas.Domain.Entities;

public enum SalaryRecordSource
{
    /// <summary>Created with the contract; captures the hiring terms.</summary>
    Initial = 0,

    /// <summary>Created by an approved contract amendment.</summary>
    Amendment = 1,
}

/// <summary>
/// An immutable snapshot of a contract's terms (salary and job title) from a
/// given effective date. Records are only ever appended — never updated or
/// deleted — so the full compensation history of a contract is auditable.
/// Payroll pays the terms effective for each period.
/// </summary>
public class SalaryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    /// <summary>Gross monthly salary in the contract's currency from EffectiveDate.</summary>
    public required decimal MonthlySalary { get; set; }

    public required string JobTitle { get; set; }

    /// <summary>First calendar day these terms apply.</summary>
    public required DateOnly EffectiveDate { get; set; }

    public SalaryRecordSource Source { get; set; } = SalaryRecordSource.Initial;

    /// <summary>The amendment that produced this record, when Source is Amendment.</summary>
    public Guid? AmendmentId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Picks the record in effect for a calendar month: the latest EffectiveDate
    /// on or before the month's end (ties broken by creation time). Mid-month
    /// changes therefore apply to that whole month (simplified: no proration).
    /// </summary>
    public static SalaryRecord? EffectiveForMonth(IEnumerable<SalaryRecord> records, int year, int month)
    {
        var monthEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        return records
            .Where(r => r.EffectiveDate <= monthEnd)
            .OrderBy(r => r.EffectiveDate)
            .ThenBy(r => r.CreatedAtUtc)
            .LastOrDefault();
    }
}
