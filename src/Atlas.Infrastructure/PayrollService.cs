using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure;

/// <summary>
/// Orchestrates payroll: creating a run computes a payslip for every contract that
/// covers the month; completing a run issues one invoice per client involved.
/// </summary>
public class PayrollService
{
    private readonly AtlasDbContext _db;

    public PayrollService(AtlasDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Creates a draft payroll run for the country/month and computes its payslips.
    /// Contracts are paid for a full month when their employment period covers any
    /// part of that calendar month (simplified: no day-based proration).
    /// </summary>
    public async Task<PayrollRun> CreateRunAsync(string countryCode, int year, int month)
    {
        var country = await _db.Countries.FindAsync(countryCode)
            ?? throw new DomainException($"Country '{countryCode}' is not supported.");

        var exists = await _db.PayrollRuns.AnyAsync(r =>
            r.CountryCode == country.Code && r.Year == year && r.Month == month);
        if (exists)
        {
            throw new DomainException($"A payroll run for {country.Code} {year}-{month:00} already exists.");
        }

        // Candidate contracts (activated at some point, right country); the precise
        // month-coverage check is domain logic evaluated in memory.
        var candidates = await _db.Contracts
            .Where(c => c.CountryCode == country.Code && c.Status != ContractStatus.Draft)
            .ToListAsync();
        var payable = candidates.Where(c => c.CoversMonth(year, month)).ToList();

        if (payable.Count == 0)
        {
            throw new DomainException($"No payable contracts in {country.Code} for {year}-{month:00}.");
        }

        var run = new PayrollRun { CountryCode = country.Code, Year = year, Month = month };

        foreach (var contract in payable)
        {
            var amounts = PayrollCalculator.Calculate(contract.MonthlySalary, country);
            run.Payslips.Add(new Payslip
            {
                PayrollRunId = run.Id,
                ContractId = contract.Id,
                WorkerId = contract.WorkerId,
                ClientId = contract.ClientId,
                CurrencyCode = contract.CurrencyCode,
                GrossSalary = amounts.Gross,
                EmployerCost = amounts.EmployerCost,
                EmployeeDeductions = amounts.EmployeeDeductions,
                NetPay = amounts.NetPay,
                TotalCost = amounts.TotalCost,
            });
        }

        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>
    /// Completes a draft run and issues one invoice per client:
    /// payroll subtotal (gross + employer costs) plus the client's management fee on gross.
    /// </summary>
    public async Task<(PayrollRun Run, List<Invoice> Invoices)> CompleteRunAsync(Guid runId)
    {
        var run = await _db.PayrollRuns
            .Include(r => r.Payslips)
            .SingleOrDefaultAsync(r => r.Id == runId)
            ?? throw new KeyNotFoundException($"Payroll run '{runId}' does not exist.");

        run.Complete(DateTimeOffset.UtcNow); // throws DomainException unless Draft

        var clientIds = run.Payslips.Select(p => p.ClientId).Distinct().ToList();
        var clients = await _db.Clients.Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        var invoices = new List<Invoice>();
        var sequence = 1;
        foreach (var clientId in clientIds.OrderBy(id => clients[id].Name))
        {
            var client = clients[clientId];
            var slips = run.Payslips.Where(p => p.ClientId == clientId).ToList();
            var subtotal = slips.Sum(p => p.TotalCost);
            var grossSum = slips.Sum(p => p.GrossSalary);
            var fee = PayrollCalculator.RoundMoney(grossSum * client.ManagementFeeRate);

            invoices.Add(new Invoice
            {
                InvoiceNumber = $"INV-{run.Year}{run.Month:00}-{run.CountryCode}-{sequence:000}",
                ClientId = clientId,
                PayrollRunId = run.Id,
                CurrencyCode = slips[0].CurrencyCode,
                PayrollSubtotal = subtotal,
                ManagementFee = fee,
                Total = subtotal + fee,
            });
            sequence++;
        }

        _db.Invoices.AddRange(invoices);
        await _db.SaveChangesAsync();
        return (run, invoices);
    }
}
