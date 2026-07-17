using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workers");

        group.MapGet("/", async (string? countryCode, AtlasDbContext db) =>
        {
            var query = db.Workers.AsQueryable();
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                var code = countryCode.Trim().ToUpperInvariant();
                query = query.Where(w => w.CountryCode == code);
            }

            return Results.Ok(await query.OrderBy(w => w.FullName).Select(w => ToResponse(w)).ToListAsync());
        });

        group.MapGet("/{id:guid}", async (Guid id, AtlasDbContext db) =>
        {
            var worker = await db.Workers.FindAsync(id);
            return worker is null ? Results.NotFound() : Results.Ok(ToResponse(worker));
        });

        group.MapPost("/", async (CreateWorkerRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.FullName))
            {
                errors["fullName"] = ["FullName is required."];
            }
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            {
                errors["email"] = ["A valid email is required."];
            }
            if (string.IsNullOrWhiteSpace(request.CountryCode))
            {
                errors["countryCode"] = ["CountryCode is required."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var countryCode = request.CountryCode!.Trim().ToUpperInvariant();
            if (!await db.Countries.AnyAsync(c => c.Code == countryCode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["countryCode"] = [$"Country '{countryCode}' is not supported."],
                });
            }

            var email = request.Email!.Trim().ToLowerInvariant();
            if (await db.Workers.AnyAsync(w => w.Email == email))
            {
                return Results.Conflict(new { detail = $"A worker with email '{email}' already exists." });
            }

            var worker = new Worker
            {
                FullName = request.FullName!.Trim(),
                Email = email,
                CountryCode = countryCode,
                DateOfBirth = request.DateOfBirth,
            };
            db.Workers.Add(worker);
            await db.SaveChangesAsync();

            return Results.Created($"/api/workers/{worker.Id}", ToResponse(worker));
        });

        return app;
    }

    private static WorkerResponse ToResponse(Worker w) =>
        new(w.Id, w.FullName, w.Email, w.CountryCode, w.DateOfBirth, w.CreatedAtUtc);
}

public record CreateWorkerRequest(
    string? FullName,
    string? Email,
    string? CountryCode,
    DateOnly? DateOfBirth);

public record WorkerResponse(
    Guid Id,
    string FullName,
    string Email,
    string CountryCode,
    DateOnly? DateOfBirth,
    DateTimeOffset CreatedAtUtc);
