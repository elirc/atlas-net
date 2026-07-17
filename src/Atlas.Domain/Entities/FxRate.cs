namespace Atlas.Domain.Entities;

/// <summary>
/// A dated exchange rate: 1 unit of the base currency buys <see cref="Rate"/>
/// units of the quote currency from <see cref="EffectiveDate"/>. Rates are
/// append-only; invoicing converts at the rate effective for the payroll period.
/// </summary>
public class FxRate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ISO 4217 code of the currency being converted from.</summary>
    public required string BaseCurrencyCode { get; set; }

    /// <summary>ISO 4217 code of the currency being converted to.</summary>
    public required string QuoteCurrencyCode { get; set; }

    /// <summary>Units of quote currency per 1 unit of base currency; positive.</summary>
    public required decimal Rate { get; set; }

    /// <summary>First calendar day this rate applies.</summary>
    public required DateOnly EffectiveDate { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Picks the rate effective for a calendar month: the latest EffectiveDate on
    /// or before the month's end (ties broken by creation time) — the same
    /// period-selection rule payroll uses for salary records.
    /// </summary>
    public static FxRate? EffectiveForMonth(IEnumerable<FxRate> rates, int year, int month)
    {
        var monthEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        return rates
            .Where(r => r.EffectiveDate <= monthEnd)
            .OrderBy(r => r.EffectiveDate)
            .ThenBy(r => r.CreatedAtUtc)
            .LastOrDefault();
    }
}
