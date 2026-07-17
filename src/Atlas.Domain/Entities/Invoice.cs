namespace Atlas.Domain.Entities;

/// <summary>
/// A client invoice for one completed payroll run: the payroll cost of the
/// client's workers in that country/month plus Atlas's management fee.
/// </summary>
public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable number, e.g. INV-202607-PH-001.</summary>
    public required string InvoiceNumber { get; set; }

    public required Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public required Guid PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }

    /// <summary>ISO 4217 currency of all amounts (the payroll country's currency).</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>Sum of TotalCost (gross + employer cost) across the client's payslips.</summary>
    public required decimal PayrollSubtotal { get; set; }

    /// <summary>Client's management fee rate applied to the gross payroll.</summary>
    public required decimal ManagementFee { get; set; }

    public required decimal Total { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
