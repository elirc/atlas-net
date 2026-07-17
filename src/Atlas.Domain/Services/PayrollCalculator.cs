using Atlas.Domain.Entities;

namespace Atlas.Domain.Services;

/// <summary>The money amounts for one worker for one payroll month.</summary>
/// <param name="Gross">Gross monthly salary.</param>
/// <param name="EmployerCost">Statutory employer cost on top of gross.</param>
/// <param name="EmployeeDeductions">Withheld from the worker's gross.</param>
/// <param name="NetPay">Paid out to the worker: gross - deductions.</param>
/// <param name="TotalCost">What employing the worker costs: gross + employer cost.</param>
public record PayrollAmounts(
    decimal Gross,
    decimal EmployerCost,
    decimal EmployeeDeductions,
    decimal NetPay,
    decimal TotalCost);

/// <summary>
/// Pure payroll math. All amounts are rounded to 2 decimal places
/// (away from zero) at each step, mirroring how statutory amounts are stated.
/// </summary>
public static class PayrollCalculator
{
    public static decimal RoundMoney(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static PayrollAmounts Calculate(decimal grossMonthlySalary, Country country)
    {
        if (grossMonthlySalary <= 0)
        {
            throw new DomainException("Gross salary must be greater than zero.");
        }

        var gross = RoundMoney(grossMonthlySalary);
        var employerCost = RoundMoney(gross * country.EmployerCostRate);
        var deductions = RoundMoney(gross * country.EmployeeDeductionRate);
        var net = gross - deductions;
        var totalCost = gross + employerCost;

        return new PayrollAmounts(gross, employerCost, deductions, net, totalCost);
    }
}
