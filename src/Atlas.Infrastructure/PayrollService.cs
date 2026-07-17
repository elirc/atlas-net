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
    /// part of that calendar month (simplified: no day-based proration). Approved,
    /// not-yet-reimbursed expense claims are paid out with the run and marked
    /// Reimbursed (added untaxed to net pay and billed on to the client).
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

        var payableIds = payable.Select(c => c.Id).ToList();
        var claimsToReimburse = await _db.ExpenseClaims
            .Include(e => e.Items)
            .Where(e => payableIds.Contains(e.ContractId) && e.Status == ExpenseClaimStatus.Approved)
            .ToListAsync();
        var salaryRecords = await _db.SalaryRecords
            .Where(r => payableIds.Contains(r.ContractId))
            .ToListAsync();
        var enrollments = await _db.BenefitEnrollments
            .Include(e => e.BenefitPlan)
            .Where(e => payableIds.Contains(e.ContractId))
            .ToListAsync();
        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var contract in payable)
        {
            // Pay the terms effective for this period, not the contract's current
            // terms — future-dated amendments must not leak into earlier months.
            var effective = SalaryRecord.EffectiveForMonth(
                salaryRecords.Where(r => r.ContractId == contract.Id), year, month);
            var salary = effective?.MonthlySalary ?? contract.MonthlySalary;
            var amounts = PayrollCalculator.Calculate(salary, country);

            var claims = claimsToReimburse.Where(e => e.ContractId == contract.Id).ToList();
            var reimbursements = PayrollCalculator.RoundMoney(claims.Sum(e => e.TotalAmount));
            foreach (var claim in claims)
            {
                claim.MarkReimbursed(run.Id, nowUtc);
            }

            // Benefit premiums are charged for every enrollment covering the month:
            // employer share is billed to the client, employee share withheld from net.
            var covering = enrollments
                .Where(e => e.ContractId == contract.Id && e.CoversMonth(year, month))
                .ToList();
            var benefitsEmployer = covering.Sum(e => e.BenefitPlan!.EmployerShare);
            var benefitsEmployee = covering.Sum(e => e.BenefitPlan!.EmployeeShare);

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
                Reimbursements = reimbursements,
                BenefitsEmployerCost = benefitsEmployer,
                BenefitsEmployeeDeduction = benefitsEmployee,
                NetPay = amounts.NetPay - benefitsEmployee + reimbursements,
                TotalCost = amounts.TotalCost + benefitsEmployer + reimbursements,
            });
        }

        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>
    /// Completes a draft run and issues one invoice per client: payroll subtotal
    /// (gross + employer costs + reimbursements) plus the client's management fee
    /// on gross, in the payroll country's currency, converted into the client's
    /// billing currency at the FX rate effective for the payroll period.
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

        var localCurrency = run.Payslips[0].CurrencyCode;
        var billingCurrencies = clients.Values
            .Select(c => c.BillingCurrencyCode)
            .Where(code => code != localCurrency)
            .Distinct()
            .ToList();
        var rates = await _db.FxRates
            .Where(r => r.BaseCurrencyCode == localCurrency && billingCurrencies.Contains(r.QuoteCurrencyCode))
            .ToListAsync();

        var invoices = new List<Invoice>();
        var sequence = 1;
        foreach (var clientId in clientIds.OrderBy(id => clients[id].Name))
        {
            var client = clients[clientId];
            var slips = run.Payslips.Where(p => p.ClientId == clientId).ToList();
            var subtotal = slips.Sum(p => p.TotalCost);
            var grossSum = slips.Sum(p => p.GrossSalary);
            var fee = PayrollCalculator.RoundMoney(grossSum * client.ManagementFeeRate);
            var total = subtotal + fee;

            var fxRate = 1m;
            if (client.BillingCurrencyCode != localCurrency)
            {
                var effective = FxRate.EffectiveForMonth(
                    rates.Where(r => r.QuoteCurrencyCode == client.BillingCurrencyCode), run.Year, run.Month)
                    ?? throw new DomainException(
                        $"No FX rate from {localCurrency} to {client.BillingCurrencyCode} is effective for {run.Year}-{run.Month:00}; " +
                        $"add one before completing the run.");
                fxRate = effective.Rate;
            }

            invoices.Add(new Invoice
            {
                InvoiceNumber = $"INV-{run.Year}{run.Month:00}-{run.CountryCode}-{sequence:000}",
                ClientId = clientId,
                PayrollRunId = run.Id,
                CurrencyCode = localCurrency,
                PayrollSubtotal = subtotal,
                ManagementFee = fee,
                Total = total,
                BillingCurrencyCode = client.BillingCurrencyCode,
                FxRateApplied = fxRate,
                TotalInBillingCurrency = PayrollCalculator.RoundMoney(total * fxRate),
            });
            sequence++;
        }

        _db.Invoices.AddRange(invoices);
        await _db.SaveChangesAsync();
        return (run, invoices);
    }
}
