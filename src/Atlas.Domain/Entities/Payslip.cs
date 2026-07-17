namespace Atlas.Domain.Entities;

/// <summary>One worker's pay for one payroll run (one calendar month).</summary>
public class Payslip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    /// <summary>Denormalized from the contract so payslips and invoices survive lookups cheaply.</summary>
    public required Guid WorkerId { get; set; }

    /// <summary>Denormalized from the contract; invoices aggregate per client.</summary>
    public required Guid ClientId { get; set; }

    /// <summary>ISO 4217 currency of all amounts on this payslip.</summary>
    public required string CurrencyCode { get; set; }

    public required decimal GrossSalary { get; set; }
    public required decimal EmployerCost { get; set; }
    public required decimal EmployeeDeductions { get; set; }
    public required decimal NetPay { get; set; }

    /// <summary>Gross + employer cost: what the client is billed for this worker.</summary>
    public required decimal TotalCost { get; set; }
}
