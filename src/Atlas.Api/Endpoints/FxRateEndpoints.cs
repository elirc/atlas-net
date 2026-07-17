using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class FxRateEndpoints
{
    public static IEndpointRouteBuilder MapFxRateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fx-rates").RequireAuthorization();

        group.MapGet("/", async (string? baseCurrency, string? quoteCurrency, AtlasDbContext db) =>
        {
            var query = db.FxRates.AsQueryable();
            if (!string.IsNullOrWhiteSpace(baseCurrency))
            {
                var code = baseCurrency.Trim().ToUpperInvariant();
                query = query.Where(r => r.BaseCurrencyCode == code);
            }
            if (!string.IsNullOrWhiteSpace(quoteCurrency))
            {
                var code = quoteCurrency.Trim().ToUpperInvariant();
                query = query.Where(r => r.QuoteCurrencyCode == code);
            }

            var rates = await query
                .OrderBy(r => r.BaseCurrencyCode).ThenBy(r => r.QuoteCurrencyCode).ThenBy(r => r.EffectiveDate)
                .ToListAsync();
            return Results.Ok(rates.Select(ToResponse).ToList());
        });

        group.MapPost("/", async (CreateFxRateRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode) || request.BaseCurrencyCode.Trim().Length != 3)
            {
                errors["baseCurrencyCode"] = ["BaseCurrencyCode must be a 3-letter ISO 4217 code."];
            }
            if (string.IsNullOrWhiteSpace(request.QuoteCurrencyCode) || request.QuoteCurrencyCode.Trim().Length != 3)
            {
                errors["quoteCurrencyCode"] = ["QuoteCurrencyCode must be a 3-letter ISO 4217 code."];
            }
            if (request.Rate <= 0)
            {
                errors["rate"] = ["Rate must be greater than zero."];
            }
            if (request.EffectiveDate == default)
            {
                errors["effectiveDate"] = ["EffectiveDate is required."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var baseCode = request.BaseCurrencyCode!.Trim().ToUpperInvariant();
            var quoteCode = request.QuoteCurrencyCode!.Trim().ToUpperInvariant();
            if (baseCode == quoteCode)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["quoteCurrencyCode"] = ["Base and quote currencies must differ."],
                });
            }

            var exists = await db.FxRates.AnyAsync(r =>
                r.BaseCurrencyCode == baseCode
                && r.QuoteCurrencyCode == quoteCode
                && r.EffectiveDate == request.EffectiveDate);
            if (exists)
            {
                return Results.Conflict(new
                {
                    detail = $"An FX rate {baseCode}->{quoteCode} effective {request.EffectiveDate:yyyy-MM-dd} already exists.",
                });
            }

            var rate = new FxRate
            {
                BaseCurrencyCode = baseCode,
                QuoteCurrencyCode = quoteCode,
                Rate = request.Rate,
                EffectiveDate = request.EffectiveDate,
            };
            db.FxRates.Add(rate);
            await db.SaveChangesAsync();

            return Results.Created($"/api/fx-rates/{rate.Id}", ToResponse(rate));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);

        return app;
    }

    private static FxRateResponse ToResponse(FxRate r) =>
        new(r.Id, r.BaseCurrencyCode, r.QuoteCurrencyCode, r.Rate, r.EffectiveDate, r.CreatedAtUtc);
}

public record CreateFxRateRequest(
    string? BaseCurrencyCode,
    string? QuoteCurrencyCode,
    decimal Rate,
    DateOnly EffectiveDate);

public record FxRateResponse(
    Guid Id,
    string BaseCurrencyCode,
    string QuoteCurrencyCode,
    decimal Rate,
    DateOnly EffectiveDate,
    DateTimeOffset CreatedAtUtc);
