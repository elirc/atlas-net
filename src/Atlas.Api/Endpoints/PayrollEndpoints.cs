using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class PayrollEndpoints
{
    public static IEndpointRouteBuilder MapPayrollEndpoints(this IEndpointRouteBuilder app)
    {
        // Payroll runs span every client in a country; they are platform operations.
        var runs = app.MapGroup("/api/payroll-runs").RequireAuthorization(AuthPolicies.PlatformAdmin);

        runs.MapGet("/", async (string? countryCode, int? page, int? pageSize, HttpContext http, AtlasDbContext db) =>
        {
            if (Pagination.Validate(page, pageSize, out var paging) is { } problem)
            {
                return problem;
            }

            var query = db.PayrollRuns.Include(r => r.Payslips).AsQueryable();
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                var code = countryCode.Trim().ToUpperInvariant();
                query = query.Where(r => r.CountryCode == code);
            }

            var results = await query
                .OrderBy(r => r.Year).ThenBy(r => r.Month).ThenBy(r => r.CountryCode)
                .ToPageAsync(http, paging);
            return Results.Ok(results.Select(ToSummary).ToList());
        });

        runs.MapGet("/{id:guid}", async (Guid id, AtlasDbContext db) =>
        {
            var run = await db.PayrollRuns
                .Include(r => r.Payslips)
                .SingleOrDefaultAsync(r => r.Id == id);
            return run is null ? Results.NotFound() : Results.Ok(ToDetail(run));
        });

        runs.MapPost("/", async (CreatePayrollRunRequest request, PayrollService payroll) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.CountryCode))
            {
                errors["countryCode"] = ["CountryCode is required."];
            }
            if (request.Year is < 2000 or > 2100)
            {
                errors["year"] = ["Year must be between 2000 and 2100."];
            }
            if (request.Month is < 1 or > 12)
            {
                errors["month"] = ["Month must be between 1 and 12."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            try
            {
                var run = await payroll.CreateRunAsync(
                    request.CountryCode!.Trim().ToUpperInvariant(), request.Year, request.Month);
                return Results.Created($"/api/payroll-runs/{run.Id}", ToDetail(run));
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        runs.MapPost("/{id:guid}/complete", async (Guid id, PayrollService payroll) =>
        {
            try
            {
                var (run, invoices) = await payroll.CompleteRunAsync(id);
                return Results.Ok(new PayrollRunCompletedResponse(
                    ToSummary(run),
                    invoices.Select(InvoiceEndpoints.ToResponse).ToList()));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return app;
    }

    private static PayrollRunSummaryResponse ToSummary(PayrollRun r) => new(
        r.Id,
        r.CountryCode,
        r.Year,
        r.Month,
        r.Status.ToString(),
        r.Payslips.Count,
        r.Payslips.Sum(p => p.GrossSalary),
        r.Payslips.Sum(p => p.EmployerCost),
        r.Payslips.Sum(p => p.Reimbursements),
        r.Payslips.Sum(p => p.BenefitsEmployerCost),
        r.Payslips.Sum(p => p.BenefitsEmployeeDeduction),
        r.Payslips.Sum(p => p.NetPay),
        r.Payslips.Sum(p => p.TotalCost),
        r.CreatedAtUtc,
        r.CompletedAtUtc);

    private static PayrollRunDetailResponse ToDetail(PayrollRun r) => new(
        ToSummary(r),
        r.Payslips
            .OrderBy(p => p.ContractId)
            .Select(p => new PayslipResponse(
                p.Id, p.PayrollRunId, p.ContractId, p.WorkerId, p.ClientId, p.CurrencyCode,
                p.GrossSalary, p.EmployerCost, p.EmployeeDeductions, p.Reimbursements,
                p.BenefitsEmployerCost, p.BenefitsEmployeeDeduction, p.UnusedLeavePayout, p.NetPay, p.TotalCost))
            .ToList());
}

public record CreatePayrollRunRequest(string? CountryCode, int Year, int Month);

public record PayrollRunSummaryResponse(
    Guid Id,
    string CountryCode,
    int Year,
    int Month,
    string Status,
    int PayslipCount,
    decimal TotalGross,
    decimal TotalEmployerCost,
    decimal TotalReimbursements,
    decimal TotalBenefitsEmployerCost,
    decimal TotalBenefitsEmployeeDeductions,
    decimal TotalNet,
    decimal TotalCost,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public record PayslipResponse(
    Guid Id,
    Guid PayrollRunId,
    Guid ContractId,
    Guid WorkerId,
    Guid ClientId,
    string CurrencyCode,
    decimal GrossSalary,
    decimal EmployerCost,
    decimal EmployeeDeductions,
    decimal Reimbursements,
    decimal BenefitsEmployerCost,
    decimal BenefitsEmployeeDeduction,
    decimal UnusedLeavePayout,
    decimal NetPay,
    decimal TotalCost);

public record PayrollRunDetailResponse(
    PayrollRunSummaryResponse Run,
    List<PayslipResponse> Payslips);

public record PayrollRunCompletedResponse(
    PayrollRunSummaryResponse Run,
    List<InvoiceResponse> Invoices);
