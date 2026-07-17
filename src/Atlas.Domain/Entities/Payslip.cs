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

    /// <summary>Approved expense claims paid out with this payslip (untaxed pass-through).</summary>
    public decimal Reimbursements { get; set; }

    /// <summary>Employer share of benefit premiums for the month; billed to the client.</summary>
    public decimal BenefitsEmployerCost { get; set; }

    /// <summary>Employee share of benefit premiums; deducted from the worker's net pay.</summary>
    public decimal BenefitsEmployeeDeduction { get; set; }

    /// <summary>Paid to the worker: gross - deductions - benefit deductions + reimbursements.</summary>
    public required decimal NetPay { get; set; }

    /// <summary>Gross + employer cost + employer benefits + reimbursements: what the client is billed.</summary>
    public required decimal TotalCost { get; set; }
}
