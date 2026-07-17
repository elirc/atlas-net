using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class CountryEndpoints
{
    public static IEndpointRouteBuilder MapCountryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/countries");

        group.MapGet("/", async (AtlasDbContext db) =>
            Results.Ok(await db.Countries.OrderBy(c => c.Code).Select(c => ToResponse(c)).ToListAsync()));

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
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var code = request.Code!.Trim().ToUpperInvariant();
            if (await db.Countries.AnyAsync(c => c.Code == code))
            {
                return Results.Conflict(new { detail = $"Country '{code}' already exists." });
            }

            var country = new Country
            {
                Code = code,
                Name = request.Name!.Trim(),
                CurrencyCode = request.CurrencyCode!.Trim().ToUpperInvariant(),
                EmployerCostRate = request.EmployerCostRate,
                EmployeeDeductionRate = request.EmployeeDeductionRate,
            };
            db.Countries.Add(country);
            await db.SaveChangesAsync();

            return Results.Created($"/api/countries/{country.Code}", ToResponse(country));
        });

        return app;
    }

    private static CountryResponse ToResponse(Country c) =>
        new(c.Code, c.Name, c.CurrencyCode, c.EmployerCostRate, c.EmployeeDeductionRate, c.IsActive);
}

public record CreateCountryRequest(
    string? Code,
    string? Name,
    string? CurrencyCode,
    decimal EmployerCostRate,
    decimal EmployeeDeductionRate);

public record CountryResponse(
    string Code,
    string Name,
    string CurrencyCode,
    decimal EmployerCostRate,
    decimal EmployeeDeductionRate,
    bool IsActive);
