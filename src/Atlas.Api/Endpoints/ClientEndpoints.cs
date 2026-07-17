using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class ClientEndpoints
{
    public static IEndpointRouteBuilder MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clients").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var query = db.Clients.AsQueryable();
            if (!user.IsPlatformAdmin())
            {
                var ownClientId = user.ClientIdOrNull();
                query = query.Where(c => c.Id == ownClientId);
            }

            return Results.Ok(await query.OrderBy(c => c.Name).Select(c => ToResponse(c)).ToListAsync());
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var client = await db.Clients.FindAsync(id);
            return client is null || !user.CanViewClient(client.Id)
                ? Results.NotFound()
                : Results.Ok(ToResponse(client));
        });

        group.MapPost("/", async (CreateClientRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }
            if (string.IsNullOrWhiteSpace(request.BillingEmail) || !request.BillingEmail.Contains('@'))
            {
                errors["billingEmail"] = ["A valid billing email is required."];
            }
            if (string.IsNullOrWhiteSpace(request.HeadquartersCountryCode))
            {
                errors["headquartersCountryCode"] = ["HeadquartersCountryCode is required."];
            }
            if (request.ManagementFeeRate is < 0 or >= 1)
            {
                errors["managementFeeRate"] = ["ManagementFeeRate must be a fraction in [0, 1)."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var countryCode = request.HeadquartersCountryCode!.Trim().ToUpperInvariant();
            if (!await db.Countries.AnyAsync(c => c.Code == countryCode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["headquartersCountryCode"] = [$"Country '{countryCode}' is not supported."],
                });
            }

            var client = new Client
            {
                Name = request.Name!.Trim(),
                LegalName = string.IsNullOrWhiteSpace(request.LegalName) ? request.Name!.Trim() : request.LegalName.Trim(),
                BillingEmail = request.BillingEmail!.Trim(),
                HeadquartersCountryCode = countryCode,
                ManagementFeeRate = request.ManagementFeeRate ?? 0.10m,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            return Results.Created($"/api/clients/{client.Id}", ToResponse(client));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);

        return app;
    }

    private static ClientResponse ToResponse(Client c) =>
        new(c.Id, c.Name, c.LegalName, c.BillingEmail, c.HeadquartersCountryCode, c.ManagementFeeRate, c.CreatedAtUtc);
}

public record CreateClientRequest(
    string? Name,
    string? LegalName,
    string? BillingEmail,
    string? HeadquartersCountryCode,
    decimal? ManagementFeeRate);

public record ClientResponse(
    Guid Id,
    string Name,
    string LegalName,
    string BillingEmail,
    string HeadquartersCountryCode,
    decimal ManagementFeeRate,
    DateTimeOffset CreatedAtUtc);
