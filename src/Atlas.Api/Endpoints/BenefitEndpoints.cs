using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class BenefitEndpoints
{
    public static IEndpointRouteBuilder MapBenefitEndpoints(this IEndpointRouteBuilder app)
    {
        MapPlanEndpoints(app);
        MapEnrollmentEndpoints(app);
        return app;
    }

    private static void MapPlanEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/benefit-plans").RequireAuthorization();

        group.MapGet("/", async (string? countryCode, AtlasDbContext db) =>
        {
            var query = db.BenefitPlans.AsQueryable();
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                var code = countryCode.Trim().ToUpperInvariant();
                query = query.Where(p => p.CountryCode == code);
            }

            var plans = await query.OrderBy(p => p.CountryCode).ThenBy(p => p.Name).ToListAsync();
            return Results.Ok(plans.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, AtlasDbContext db) =>
        {
            var plan = await db.BenefitPlans.FindAsync(id);
            return plan is null ? Results.NotFound() : Results.Ok(ToResponse(plan));
        });

        group.MapPost("/", async (CreateBenefitPlanRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.CountryCode))
            {
                errors["countryCode"] = ["CountryCode is required."];
            }
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }
            if (request.MonthlyCost <= 0)
            {
                errors["monthlyCost"] = ["MonthlyCost must be greater than zero."];
            }
            if (request.EmployerContributionRate is < 0 or > 1)
            {
                errors["employerContributionRate"] = ["EmployerContributionRate must be a fraction in [0, 1]."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var code = request.CountryCode!.Trim().ToUpperInvariant();
            if (!await db.Countries.AnyAsync(c => c.Code == code))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["countryCode"] = [$"Country '{code}' is not supported."],
                });
            }

            var name = request.Name!.Trim();
            if (await db.BenefitPlans.AnyAsync(p => p.CountryCode == code && p.Name == name))
            {
                return Results.Conflict(new { detail = $"A benefit plan named '{name}' already exists in {code}." });
            }

            var plan = new BenefitPlan
            {
                CountryCode = code,
                Name = name,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                MonthlyCost = request.MonthlyCost,
                EmployerContributionRate = request.EmployerContributionRate,
            };
            db.BenefitPlans.Add(plan);
            await db.SaveChangesAsync();

            return Results.Created($"/api/benefit-plans/{plan.Id}", ToResponse(plan));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, AtlasDbContext db) =>
        {
            var plan = await db.BenefitPlans.FindAsync(id);
            if (plan is null)
            {
                return Results.NotFound();
            }

            plan.IsActive = false;
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(plan));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);
    }

    private static void MapEnrollmentEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/benefit-enrollments").RequireAuthorization();

        group.MapGet("/", async (Guid? contractId, string? status, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var query = db.BenefitEnrollments.AsQueryable();
            if (!user.IsPlatformAdmin())
            {
                var ownClientId = user.ClientIdOrNull();
                query = query.Where(e => e.Contract!.ClientId == ownClientId);
            }
            if (contractId is not null)
            {
                query = query.Where(e => e.ContractId == contractId);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<BenefitEnrollmentStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{status}'. Expected {string.Join(", ", Enum.GetNames<BenefitEnrollmentStatus>())}."],
                    });
                }
                query = query.Where(e => e.Status == parsed);
            }

            var enrollments = await query
                .Include(e => e.BenefitPlan)
                .OrderBy(e => e.StartDate).ThenBy(e => e.CreatedAtUtc)
                .ToListAsync();
            return Results.Ok(enrollments.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var enrollment = await db.BenefitEnrollments
                .Include(e => e.Contract)
                .Include(e => e.BenefitPlan)
                .SingleOrDefaultAsync(e => e.Id == id);
            return enrollment is null || !user.CanViewClient(enrollment.Contract!.ClientId)
                ? Results.NotFound()
                : Results.Ok(ToResponse(enrollment));
        });

        group.MapPost("/", async (CreateBenefitEnrollmentRequest request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ContractId == Guid.Empty)
            {
                errors["contractId"] = ["ContractId is required."];
            }
            if (request.BenefitPlanId == Guid.Empty)
            {
                errors["benefitPlanId"] = ["BenefitPlanId is required."];
            }
            if (request.StartDate == default)
            {
                errors["startDate"] = ["StartDate is required."];
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
                    detail: "Only platform admins or the client's own admins can enroll workers in benefits.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var plan = await db.BenefitPlans.FindAsync(request.BenefitPlanId);
            if (plan is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["benefitPlanId"] = [$"Benefit plan '{request.BenefitPlanId}' does not exist."],
                });
            }

            if (contract.Status != ContractStatus.Active)
            {
                return Results.Problem(
                    detail: $"Benefits require an active contract; this contract is {contract.Status}.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (!plan.IsActive)
            {
                return Results.Problem(
                    detail: $"Benefit plan '{plan.Name}' is no longer accepting enrollments.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (plan.CountryCode != contract.CountryCode)
            {
                return Results.Problem(
                    detail: $"Benefit plan '{plan.Name}' is offered in {plan.CountryCode}, but the contract is in {contract.CountryCode}.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (request.StartDate < contract.StartDate)
            {
                return Results.Problem(
                    detail: $"Coverage cannot start before the contract start date {contract.StartDate:yyyy-MM-dd}.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var hasActive = await db.BenefitEnrollments.AnyAsync(e =>
                e.ContractId == contract.Id
                && e.BenefitPlanId == plan.Id
                && e.Status == BenefitEnrollmentStatus.Active);
            if (hasActive)
            {
                return Results.Problem(
                    detail: $"This contract already has an active enrollment in '{plan.Name}'.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var enrollment = new BenefitEnrollment
            {
                ContractId = contract.Id,
                BenefitPlanId = plan.Id,
                StartDate = request.StartDate,
                BenefitPlan = plan,
            };
            db.BenefitEnrollments.Add(enrollment);
            await db.SaveChangesAsync();

            return Results.Created($"/api/benefit-enrollments/{enrollment.Id}", ToResponse(enrollment));
        });

        group.MapPost("/{id:guid}/end", async (
            Guid id, EndBenefitEnrollmentRequest request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var enrollment = await db.BenefitEnrollments
                .Include(e => e.Contract)
                .Include(e => e.BenefitPlan)
                .SingleOrDefaultAsync(e => e.Id == id);
            if (enrollment is null || !user.CanViewClient(enrollment.Contract!.ClientId))
            {
                return Results.NotFound();
            }
            if (!user.CanManageClient(enrollment.Contract!.ClientId))
            {
                return Results.Problem(
                    detail: "Only platform admins or the client's own admins can end enrollments.",
                    statusCode: StatusCodes.Status403Forbidden);
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
                enrollment.End(request.EndDate, DateTimeOffset.UtcNow);
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(enrollment));
        });
    }

    private static BenefitPlanResponse ToResponse(BenefitPlan p) => new(
        p.Id,
        p.CountryCode,
        p.Name,
        p.Description,
        p.MonthlyCost,
        p.EmployerContributionRate,
        p.EmployerShare,
        p.EmployeeShare,
        p.IsActive,
        p.CreatedAtUtc);

    private static BenefitEnrollmentResponse ToResponse(BenefitEnrollment e) => new(
        e.Id,
        e.ContractId,
        e.BenefitPlanId,
        e.BenefitPlan?.Name ?? string.Empty,
        e.StartDate,
        e.EndDate,
        e.Status.ToString(),
        e.CreatedAtUtc,
        e.EndedAtUtc);
}

public record CreateBenefitPlanRequest(
    string? CountryCode,
    string? Name,
    string? Description,
    decimal MonthlyCost,
    decimal EmployerContributionRate);

public record BenefitPlanResponse(
    Guid Id,
    string CountryCode,
    string Name,
    string? Description,
    decimal MonthlyCost,
    decimal EmployerContributionRate,
    decimal EmployerShare,
    decimal EmployeeShare,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);

public record CreateBenefitEnrollmentRequest(
    Guid ContractId,
    Guid BenefitPlanId,
    DateOnly StartDate);

public record EndBenefitEnrollmentRequest(DateOnly EndDate);

public record BenefitEnrollmentResponse(
    Guid Id,
    Guid ContractId,
    Guid BenefitPlanId,
    string BenefitPlanName,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EndedAtUtc);
