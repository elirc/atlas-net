using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts");

        group.MapGet("/", async (Guid? clientId, string? status, AtlasDbContext db) =>
        {
            var query = db.Contracts.AsQueryable();
            if (clientId is not null)
            {
                query = query.Where(c => c.ClientId == clientId);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ContractStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{status}'. Expected Draft, Active, or Terminated."],
                    });
                }
                query = query.Where(c => c.Status == parsed);
            }

            var contracts = await query.OrderBy(c => c.CreatedAtUtc).ToListAsync();
            return Results.Ok(contracts.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, AtlasDbContext db) =>
        {
            var contract = await db.Contracts.FindAsync(id);
            return contract is null ? Results.NotFound() : Results.Ok(ToResponse(contract));
        });

        group.MapPost("/", async (CreateContractRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ClientId == Guid.Empty)
            {
                errors["clientId"] = ["ClientId is required."];
            }
            if (request.WorkerId == Guid.Empty)
            {
                errors["workerId"] = ["WorkerId is required."];
            }
            if (string.IsNullOrWhiteSpace(request.JobTitle))
            {
                errors["jobTitle"] = ["JobTitle is required."];
            }
            if (request.MonthlySalary <= 0)
            {
                errors["monthlySalary"] = ["MonthlySalary must be greater than zero."];
            }
            if (request.StartDate == default)
            {
                errors["startDate"] = ["StartDate is required."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var client = await db.Clients.FindAsync(request.ClientId);
            if (client is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = [$"Client '{request.ClientId}' does not exist."],
                });
            }

            var worker = await db.Workers.Include(w => w.Country).SingleOrDefaultAsync(w => w.Id == request.WorkerId);
            if (worker is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["workerId"] = [$"Worker '{request.WorkerId}' does not exist."],
                });
            }

            var country = worker.Country!;
            if (!country.IsActive)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["workerId"] = [$"Hiring in {country.Name} is currently unavailable."],
                });
            }

            var hasOpenContract = await db.Contracts.AnyAsync(c =>
                c.WorkerId == worker.Id && c.Status != ContractStatus.Terminated);
            if (hasOpenContract)
            {
                return Results.Conflict(new
                {
                    detail = $"Worker '{worker.FullName}' already has a contract that is not terminated.",
                });
            }

            var contract = new EmploymentContract
            {
                ClientId = client.Id,
                WorkerId = worker.Id,
                CountryCode = country.Code,
                JobTitle = request.JobTitle!.Trim(),
                MonthlySalary = request.MonthlySalary,
                CurrencyCode = country.CurrencyCode,
                StartDate = request.StartDate,
            };
            db.Contracts.Add(contract);
            db.OnboardingItems.AddRange(OnboardingItem.CreateDefaultChecklist(contract.Id));
            await db.SaveChangesAsync();

            return Results.Created($"/api/contracts/{contract.Id}", ToResponse(contract));
        });

        group.MapPost("/{id:guid}/activate", async (Guid id, AtlasDbContext db) =>
        {
            var contract = await db.Contracts.FindAsync(id);
            if (contract is null)
            {
                return Results.NotFound();
            }

            var pendingRequired = await db.OnboardingItems
                .Where(i => i.ContractId == id && i.IsRequired && !i.IsCompleted)
                .Select(i => i.Title)
                .ToListAsync();
            if (pendingRequired.Count > 0)
            {
                return Results.Problem(
                    detail: $"Contract cannot be activated: {pendingRequired.Count} required onboarding item(s) pending ({string.Join("; ", pendingRequired)}).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                contract.Activate(DateTimeOffset.UtcNow);
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(contract));
        });

        group.MapPost("/{id:guid}/terminate", async (Guid id, TerminateContractRequest request, AtlasDbContext db) =>
        {
            var contract = await db.Contracts.FindAsync(id);
            if (contract is null)
            {
                return Results.NotFound();
            }

            if (request.EndDate == default)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["endDate"] = ["EndDate is required."],
                });
            }

            try
            {
                contract.Terminate(request.EndDate, request.Reason ?? string.Empty, DateTimeOffset.UtcNow);
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(contract));
        });

        return app;
    }

    internal static ContractResponse ToResponse(EmploymentContract c) => new(
        c.Id,
        c.ClientId,
        c.WorkerId,
        c.CountryCode,
        c.JobTitle,
        c.MonthlySalary,
        c.CurrencyCode,
        c.StartDate,
        c.EndDate,
        c.Status.ToString(),
        c.CreatedAtUtc,
        c.ActivatedAtUtc,
        c.TerminatedAtUtc,
        c.TerminationReason);
}

public record CreateContractRequest(
    Guid ClientId,
    Guid WorkerId,
    string? JobTitle,
    decimal MonthlySalary,
    DateOnly StartDate);

public record TerminateContractRequest(DateOnly EndDate, string? Reason);

public record ContractResponse(
    Guid Id,
    Guid ClientId,
    Guid WorkerId,
    string CountryCode,
    string JobTitle,
    decimal MonthlySalary,
    string CurrencyCode,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? TerminatedAtUtc,
    string? TerminationReason);
