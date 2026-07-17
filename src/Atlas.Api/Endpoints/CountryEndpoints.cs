using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class CountryEndpoints
{
    public static IEndpointRouteBuilder MapCountryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/countries").RequireAuthorization();

        group.MapGet("/", async (int? page, int? pageSize, HttpContext http, AtlasDbContext db) =>
        {
            if (Pagination.Validate(page, pageSize, out var paging) is { } problem)
            {
                return problem;
            }

            var countries = await db.Countries.OrderBy(c => c.Code).ToPageAsync(http, paging);
            return Results.Ok(countries.Select(ToResponse).ToList());
        });

        group.MapGet("/{code}", async (string code, AtlasDbContext db) =>
        {
            var country = await db.Countries.FindAsync(code.ToUpperInvariant());
            return country is null ? Results.NotFound() : Results.Ok(ToResponse(country));
        });

        group.MapPost("/", async (CreateCountryRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length != 2)
            {
                errors["code"] = ["Code must be a 2-letter ISO 3166-1 alpha-2 code."];
            }
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }
            if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3)
            {
                errors["currencyCode"] = ["CurrencyCode must be a 3-letter ISO 4217 code."];
            }
            if (request.EmployerCostRate is < 0 or >= 1)
            {
                errors["employerCostRate"] = ["EmployerCostRate must be a fraction in [0, 1)."];
            }
            if (request.EmployeeDeductionRate is < 0 or >= 1)
            {
                errors["employeeDeductionRate"] = ["EmployeeDeductionRate must be a fraction in [0, 1)."];
            }
            if (request.MinimumNoticeDays is < 0 or > 365)
            {
                errors["minimumNoticeDays"] = ["MinimumNoticeDays must be between 0 and 365."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var code = request.Code!.Trim().ToUpperInvariant();
            if (await db.Countries.AnyAsync(c => c.Code == code))
            {
                return Results.Problem(
                    detail: $"Country '{code}' already exists.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var country = new Country
            {
                Code = code,
                Name = request.Name!.Trim(),
                CurrencyCode = request.CurrencyCode!.Trim().ToUpperInvariant(),
                EmployerCostRate = request.EmployerCostRate,
                EmployeeDeductionRate = request.EmployeeDeductionRate,
                MinimumNoticeDays = request.MinimumNoticeDays ?? 30,
            };
            db.Countries.Add(country);
            await db.SaveChangesAsync();

            return Results.Created($"/api/countries/{country.Code}", ToResponse(country));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);

        return app;
    }

    private static CountryResponse ToResponse(Country c) =>
        new(c.Code, c.Name, c.CurrencyCode, c.EmployerCostRate, c.EmployeeDeductionRate, c.MinimumNoticeDays, c.IsActive);
}

public record CreateCountryRequest(
    string? Code,
    string? Name,
    string? CurrencyCode,
    decimal EmployerCostRate,
    decimal EmployeeDeductionRate,
    int? MinimumNoticeDays);

public record CountryResponse(
    string Code,
    string Name,
    string CurrencyCode,
    decimal EmployerCostRate,
    decimal EmployeeDeductionRate,
    int MinimumNoticeDays,
    bool IsActive);
