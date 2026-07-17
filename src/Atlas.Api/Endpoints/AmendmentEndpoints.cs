using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class AmendmentEndpoints
{
    public static IEndpointRouteBuilder MapAmendmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contract-amendments").RequireAuthorization();

        group.MapGet("/", async (Guid? contractId, string? status, int? page, int? pageSize, ClaimsPrincipal user, HttpContext http, AtlasDbContext db) =>
        {
            if (Pagination.Validate(page, pageSize, out var paging) is { } problem)
            {
                return problem;
            }

            var query = db.ContractAmendments.AsQueryable();
            if (!user.IsPlatformAdmin())
            {
                var ownClientId = user.ClientIdOrNull();
                query = query.Where(a => a.Contract!.ClientId == ownClientId);
            }
            if (contractId is not null)
            {
                query = query.Where(a => a.ContractId == contractId);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<AmendmentStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{status}'. Expected {string.Join(", ", Enum.GetNames<AmendmentStatus>())}."],
                    });
                }
                query = query.Where(a => a.Status == parsed);
            }

            var amendments = await query.OrderBy(a => a.RequestedAtUtc).ToPageAsync(http, paging);
            return Results.Ok(amendments.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var amendment = await db.ContractAmendments.Include(a => a.Contract).SingleOrDefaultAsync(a => a.Id == id);
            return amendment is null || !user.CanViewClient(amendment.Contract!.ClientId)
                ? Results.NotFound()
                : Results.Ok(ToResponse(amendment));
        });

        group.MapPost("/", async (CreateAmendmentRequest request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ContractId == Guid.Empty)
            {
                errors["contractId"] = ["ContractId is required."];
            }
            if (request.NewMonthlySalary is null && string.IsNullOrWhiteSpace(request.NewJobTitle))
            {
                errors["newMonthlySalary"] = ["An amendment must change the salary, the job title, or both."];
            }
            if (request.NewMonthlySalary is <= 0)
            {
                errors["newMonthlySalary"] = ["NewMonthlySalary must be greater than zero."];
            }
            if (request.EffectiveDate == default)
            {
                errors["effectiveDate"] = ["EffectiveDate is required."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var contract = await db.Contracts.FindAsync(request.ContractId);
            if (contract is null || !user.CanViewClient(contract.ClientId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["contractId"] = [$"Contract '{request.ContractId}' does not exist."],
                });
            }
            if (!user.CanManageClient(contract.ClientId))
            {
                return Results.Problem(
                    detail: "Only platform admins or the client's own admins can request amendments.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (contract.Status != ContractStatus.Active)
            {
                return Results.Problem(
                    detail: $"Only active contracts can be amended; this contract is {contract.Status}.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (request.EffectiveDate < contract.StartDate)
            {
                return Results.Problem(
                    detail: $"The effective date cannot be before the contract start date {contract.StartDate:yyyy-MM-dd}.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (await db.ContractAmendments.AnyAsync(a =>
                    a.ContractId == contract.Id && a.Status == AmendmentStatus.Pending))
            {
                return Results.Problem(
                    detail: "This contract already has a pending amendment; decide or cancel it first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var amendment = new ContractAmendment
            {
                ContractId = contract.Id,
                NewMonthlySalary = request.NewMonthlySalary,
                NewJobTitle = string.IsNullOrWhiteSpace(request.NewJobTitle) ? null : request.NewJobTitle.Trim(),
                EffectiveDate = request.EffectiveDate,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            };
            db.ContractAmendments.Add(amendment);
            await db.SaveChangesAsync();

            return Results.Created($"/api/contract-amendments/{amendment.Id}", ToResponse(amendment));
        });

        group.MapPost("/{id:guid}/approve", async (
            Guid id, DecideAmendmentRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var amendment = await db.ContractAmendments.Include(a => a.Contract).SingleOrDefaultAsync(a => a.Id == id);
            if (amendment is null || !user.CanViewClient(amendment.Contract!.ClientId))
            {
                return Results.NotFound();
            }
            if (!user.CanManageClient(amendment.Contract!.ClientId))
            {
                return Results.Problem(
                    detail: "Only platform admins or the client's own admins can decide amendments.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var contract = amendment.Contract!;
            try
            {
                // Applying to the contract validates it is still active before the
                // amendment itself transitions, so a failed apply leaves it pending.
                contract.ApplyAmendment(amendment.NewMonthlySalary, amendment.NewJobTitle);
                amendment.Approve(DateTimeOffset.UtcNow, request?.Note);
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            db.SalaryRecords.Add(new SalaryRecord
            {
                ContractId = contract.Id,
                MonthlySalary = contract.MonthlySalary,
                JobTitle = contract.JobTitle,
                EffectiveDate = amendment.EffectiveDate,
                Source = SalaryRecordSource.Amendment,
                AmendmentId = amendment.Id,
            });
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(amendment));
        });

        group.MapPost("/{id:guid}/reject", async (
            Guid id, DecideAmendmentRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
                await DecideAsync(id, user, db, a => a.Reject(request?.Note ?? string.Empty, DateTimeOffset.UtcNow)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
            await DecideAsync(id, user, db, a => a.Cancel(DateTimeOffset.UtcNow)));

        app.MapGet("/api/contracts/{contractId:guid}/salary-history", async (
            Guid contractId, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var contract = await db.Contracts.FindAsync(contractId);
            if (contract is null || !user.CanViewClient(contract.ClientId))
            {
                return Results.NotFound();
            }

            var records = await db.SalaryRecords
                .Where(r => r.ContractId == contractId)
                .OrderBy(r => r.EffectiveDate).ThenBy(r => r.CreatedAtUtc)
                .ToListAsync();
            return Results.Ok(records.Select(ToResponse).ToList());
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Shared load + authorize + transition + save pipeline for reject/cancel.</summary>
    private static async Task<IResult> DecideAsync(
        Guid id, ClaimsPrincipal user, AtlasDbContext db, Action<ContractAmendment> transition)
    {
        var amendment = await db.ContractAmendments.Include(a => a.Contract).SingleOrDefaultAsync(a => a.Id == id);
        if (amendment is null || !user.CanViewClient(amendment.Contract!.ClientId))
        {
            return Results.NotFound();
        }
        if (!user.CanManageClient(amendment.Contract!.ClientId))
        {
            return Results.Problem(
                detail: "Only platform admins or the client's own admins can decide amendments.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            transition(amendment);
        }
        catch (DomainException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync();
        return Results.Ok(ToResponse(amendment));
    }

    private static AmendmentResponse ToResponse(ContractAmendment a) => new(
        a.Id,
        a.ContractId,
        a.NewMonthlySalary,
        a.NewJobTitle,
        a.EffectiveDate,
        a.Reason,
        a.Status.ToString(),
        a.RequestedAtUtc,
        a.DecidedAtUtc,
        a.DecisionNote);

    private static SalaryRecordResponse ToResponse(SalaryRecord r) => new(
        r.Id,
        r.ContractId,
        r.MonthlySalary,
        r.JobTitle,
        r.EffectiveDate,
        r.Source.ToString(),
        r.AmendmentId,
        r.CreatedAtUtc);
}

public record CreateAmendmentRequest(
    Guid ContractId,
    decimal? NewMonthlySalary,
    string? NewJobTitle,
    DateOnly EffectiveDate,
    string? Reason);

public record DecideAmendmentRequest(string? Note);

public record AmendmentResponse(
    Guid Id,
    Guid ContractId,
    decimal? NewMonthlySalary,
    string? NewJobTitle,
    DateOnly EffectiveDate,
    string? Reason,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote);

public record SalaryRecordResponse(
    Guid Id,
    Guid ContractId,
    decimal MonthlySalary,
    string JobTitle,
    DateOnly EffectiveDate,
    string Source,
    Guid? AmendmentId,
    DateTimeOffset CreatedAtUtc);
