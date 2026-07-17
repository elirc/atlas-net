using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

/// <summary>
/// Operational reports for the platform team: headcount, payroll cost trends,
/// upcoming compliance-document expiries, and invoice aging. All endpoints are
/// platform-admin only and aggregate data across every client.
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization(AuthPolicies.PlatformAdmin);

        // Workers employed on a given day: activated contracts whose employment
        // period covers the date (a terminated contract still serving out its
        // notice counts until its end date).
        group.MapGet("/headcount", async (DateOnly? asOf, AtlasDbContext db) =>
        {
            var date = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);

            var employed = await db.Contracts
                .Where(c => c.Status != ContractStatus.Draft
                            && c.StartDate <= date
                            && (c.EndDate == null || c.EndDate >= date))
                .ToListAsync();

            var clientIds = employed.Select(c => c.ClientId).Distinct().ToList();
            var clientNames = await db.Clients
                .Where(c => clientIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name);

            return Results.Ok(new HeadcountReportResponse(
                date,
                employed.Count,
                employed
                    .GroupBy(c => c.CountryCode)
                    .Select(g => new HeadcountByCountryResponse(g.Key, g.Count()))
                    .OrderBy(r => r.CountryCode)
                    .ToList(),
                employed
                    .GroupBy(c => c.ClientId)
                    .Select(g => new HeadcountByClientResponse(g.Key, clientNames[g.Key], g.Count()))
                    .OrderBy(r => r.ClientName)
                    .ToList()));
        });

        // Payroll cost totals per (year, month, currency), filterable by country,
        // client, and an inclusive from/to period range.
        group.MapGet("/payroll-costs", async (
            string? countryCode, Guid? clientId,
            int? fromYear, int? fromMonth, int? toYear, int? toMonth,
            AtlasDbContext db) =>
        {
            if (fromMonth is < 1 or > 12 || toMonth is < 1 or > 12)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fromMonth"] = ["Months must be between 1 and 12."],
                });
            }

            var query = db.Payslips.Include(p => p.PayrollRun).AsQueryable();
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                var code = countryCode.Trim().ToUpperInvariant();
                query = query.Where(p => p.PayrollRun!.CountryCode == code);
            }
            if (clientId is not null)
            {
                query = query.Where(p => p.ClientId == clientId);
            }
            if (fromYear is not null)
            {
                var from = fromYear.Value * 100 + (fromMonth ?? 1);
                query = query.Where(p => p.PayrollRun!.Year * 100 + p.PayrollRun.Month >= from);
            }
            if (toYear is not null)
            {
                var to = toYear.Value * 100 + (toMonth ?? 12);
                query = query.Where(p => p.PayrollRun!.Year * 100 + p.PayrollRun.Month <= to);
            }

            var payslips = await query.ToListAsync();
            var rows = payslips
                .GroupBy(p => new { p.PayrollRun!.Year, p.PayrollRun.Month, p.CurrencyCode })
                .Select(g => new PayrollCostRowResponse(
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.CurrencyCode,
                    g.Count(),
                    g.Sum(p => p.GrossSalary),
                    g.Sum(p => p.EmployerCost),
                    g.Sum(p => p.Reimbursements),
                    g.Sum(p => p.BenefitsEmployerCost),
                    g.Sum(p => p.UnusedLeavePayout),
                    g.Sum(p => p.TotalCost)))
                .OrderBy(r => r.Year).ThenBy(r => r.Month).ThenBy(r => r.CurrencyCode)
                .ToList();

            return Results.Ok(rows);
        });

        // Compliance documents already expired or expiring within the window.
        group.MapGet("/compliance-expiries", async (int? withinDays, AtlasDbContext db) =>
        {
            var window = withinDays ?? 60;
            if (window < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["withinDays"] = ["withinDays must be non-negative."],
                });
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var cutoff = today.AddDays(window);

            var documents = await db.ComplianceDocuments
                .Include(d => d.Worker)
                .Where(d => d.ExpiryDate != null && d.ExpiryDate <= cutoff)
                .OrderBy(d => d.ExpiryDate)
                .ToListAsync();

            return Results.Ok(documents
                .Select(d => new ComplianceExpiryRowResponse(
                    d.Id,
                    d.WorkerId,
                    d.Worker!.FullName,
                    d.Worker.CountryCode,
                    d.Type.ToString(),
                    d.Name,
                    d.ExpiryDate!.Value,
                    d.ExpiryDate.Value.DayNumber - today.DayNumber,
                    d.GetStatus(today, window).ToString()))
                .ToList());
        });

        // Outstanding invoice totals bucketed by age since issue (no payment
        // tracking yet, so every invoice counts as outstanding), grouped per
        // billing currency so totals are never mixed across currencies.
        group.MapGet("/invoice-aging", async (Guid? clientId, AtlasDbContext db) =>
        {
            var query = db.Invoices.AsQueryable();
            if (clientId is not null)
            {
                query = query.Where(i => i.ClientId == clientId);
            }

            var invoices = await query.ToListAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var rows = invoices
                .GroupBy(i => new
                {
                    Bucket = AgeBucket(today.DayNumber - DateOnly.FromDateTime(i.IssuedAtUtc.UtcDateTime).DayNumber),
                    Currency = i.BillingCurrencyCode,
                })
                .Select(g => new InvoiceAgingRowResponse(
                    g.Key.Bucket,
                    g.Key.Currency,
                    g.Count(),
                    g.Sum(i => i.TotalInBillingCurrency)))
                .OrderBy(r => BucketOrder(r.Bucket)).ThenBy(r => r.CurrencyCode)
                .ToList();

            return Results.Ok(new InvoiceAgingReportResponse(today, rows));
        });

        return app;
    }

    private static string AgeBucket(int ageDays) => ageDays switch
    {
        <= 30 => "0-30",
        <= 60 => "31-60",
        <= 90 => "61-90",
        _ => "90+",
    };

    private static int BucketOrder(string bucket) => bucket switch
    {
        "0-30" => 0,
        "31-60" => 1,
        "61-90" => 2,
        _ => 3,
    };
}

public record HeadcountByCountryResponse(string CountryCode, int Count);

public record HeadcountByClientResponse(Guid ClientId, string ClientName, int Count);

public record HeadcountReportResponse(
    DateOnly AsOf,
    int Total,
    List<HeadcountByCountryResponse> ByCountry,
    List<HeadcountByClientResponse> ByClient);

public record PayrollCostRowResponse(
    int Year,
    int Month,
    string CurrencyCode,
    int PayslipCount,
    decimal TotalGross,
    decimal TotalEmployerCost,
    decimal TotalReimbursements,
    decimal TotalBenefitsEmployerCost,
    decimal TotalUnusedLeavePayout,
    decimal TotalCost);

public record ComplianceExpiryRowResponse(
    Guid DocumentId,
    Guid WorkerId,
    string WorkerName,
    string CountryCode,
    string Type,
    string Name,
    DateOnly ExpiryDate,
    int DaysUntilExpiry,
    string Status);

public record InvoiceAgingRowResponse(
    string Bucket,
    string CurrencyCode,
    int InvoiceCount,
    decimal Total);

public record InvoiceAgingReportResponse(
    DateOnly AsOf,
    List<InvoiceAgingRowResponse> Rows);
