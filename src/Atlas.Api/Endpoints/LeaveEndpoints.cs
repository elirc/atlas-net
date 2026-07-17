using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class LeaveEndpoints
{
    public static IEndpointRouteBuilder MapLeaveEndpoints(this IEndpointRouteBuilder app)
    {
        MapPolicyEndpoints(app);
        MapRequestEndpoints(app);

        app.MapGet("/api/contracts/{contractId:guid}/leave-balances", async (
            Guid contractId, int? year, ClaimsPrincipal user, AtlasDbContext db, LeaveService leave) =>
        {
            var contract = await db.Contracts.FindAsync(contractId);
            if (contract is null || !user.CanViewClient(contract.ClientId))
            {
                return Results.NotFound();
            }

            var balanceYear = year ?? DateTime.UtcNow.Year;
            try
            {
                var balances = await leave.GetBalancesAsync(contract, balanceYear);
                return Results.Ok(new ContractLeaveBalancesResponse(
                    contractId,
                    balanceYear,
                    balances.Select(b => new LeaveBalanceResponse(
                        b.Type.ToString(), b.AllowanceDays, b.ApprovedDays, b.PendingDays, b.RemainingDays)).ToList()));
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireAuthorization();

        return app;
    }

    private static void MapPolicyEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leave-policies").RequireAuthorization();

        group.MapGet("/", async (AtlasDbContext db) =>
        {
            var policies = await db.LeavePolicies.OrderBy(p => p.CountryCode).ToListAsync();
            return Results.Ok(policies.Select(ToResponse).ToList());
        });

        group.MapGet("/{countryCode}", async (string countryCode, AtlasDbContext db) =>
        {
            var code = countryCode.Trim().ToUpperInvariant();
            var policy = await db.LeavePolicies.SingleOrDefaultAsync(p => p.CountryCode == code);
            return policy is null ? Results.NotFound() : Results.Ok(ToResponse(policy));
        });

        group.MapPost("/", async (CreateLeavePolicyRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.CountryCode))
            {
                errors["countryCode"] = ["CountryCode is required."];
            }
            if (request.AnnualLeaveDays is < 0 or > 366)
            {
                errors["annualLeaveDays"] = ["AnnualLeaveDays must be between 0 and 366."];
            }
            if (request.SickLeaveDays is < 0 or > 366)
            {
                errors["sickLeaveDays"] = ["SickLeaveDays must be between 0 and 366."];
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
            if (await db.LeavePolicies.AnyAsync(p => p.CountryCode == code))
            {
                return Results.Conflict(new { detail = $"A leave policy for {code} already exists." });
            }

            var policy = new LeavePolicy
            {
                CountryCode = code,
                AnnualLeaveDays = request.AnnualLeaveDays,
                SickLeaveDays = request.SickLeaveDays,
            };
            db.LeavePolicies.Add(policy);
            await db.SaveChangesAsync();

            return Results.Created($"/api/leave-policies/{policy.CountryCode}", ToResponse(policy));
        }).RequireAuthorization(AuthPolicies.PlatformAdmin);
    }

    private static void MapRequestEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leave-requests").RequireAuthorization();

        group.MapGet("/", async (Guid? contractId, string? status, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var query = db.LeaveRequests.AsQueryable();
            if (!user.IsPlatformAdmin())
            {
                var ownClientId = user.ClientIdOrNull();
                query = query.Where(r => r.Contract!.ClientId == ownClientId);
            }
            if (contractId is not null)
            {
                query = query.Where(r => r.ContractId == contractId);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<LeaveRequestStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{status}'. Expected {string.Join(", ", Enum.GetNames<LeaveRequestStatus>())}."],
                    });
                }
                query = query.Where(r => r.Status == parsed);
            }

            var requests = await query.OrderBy(r => r.StartDate).ThenBy(r => r.RequestedAtUtc).ToListAsync();
            return Results.Ok(requests.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var request = await db.LeaveRequests.Include(r => r.Contract).SingleOrDefaultAsync(r => r.Id == id);
            return request is null || !user.CanViewClient(request.Contract!.ClientId)
                ? Results.NotFound()
                : Results.Ok(ToResponse(request));
        });

        group.MapPost("/", async (CreateLeaveRequestRequest request, ClaimsPrincipal user, AtlasDbContext db, LeaveService leave) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ContractId == Guid.Empty)
            {
                errors["contractId"] = ["ContractId is required."];
            }
            LeaveType type = default;
            if (string.IsNullOrWhiteSpace(request.Type)
                || !Enum.TryParse(request.Type, ignoreCase: true, out type))
            {
                errors["type"] = [$"Type must be one of: {string.Join(", ", Enum.GetNames<LeaveType>())}."];
            }
            if (request.StartDate == default)
            {
                errors["startDate"] = ["StartDate is required."];
            }
            if (request.EndDate == default)
            {
                errors["endDate"] = ["EndDate is required."];
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
                    detail: "Only platform admins or the client's own admins can submit leave requests.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var created = await leave.CreateRequestAsync(
                    contract, type, request.StartDate, request.EndDate, request.Reason);
                return Results.Created($"/api/leave-requests/{created.Id}", ToResponse(created));
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapPost("/{id:guid}/approve", async (
            Guid id, DecideLeaveRequestRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
                await DecideAsync(id, user, db, r => r.Approve(DateTimeOffset.UtcNow, request?.Note)));

        group.MapPost("/{id:guid}/reject", async (
            Guid id, DecideLeaveRequestRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
                await DecideAsync(id, user, db, r => r.Reject(request?.Note ?? string.Empty, DateTimeOffset.UtcNow)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
            await DecideAsync(id, user, db, r => r.Cancel(DateTimeOffset.UtcNow)));
    }

    /// <summary>Shared load + authorize + transition + save pipeline for leave decisions.</summary>
    private static async Task<IResult> DecideAsync(
        Guid id, ClaimsPrincipal user, AtlasDbContext db, Action<LeaveRequest> transition)
    {
        var request = await db.LeaveRequests.Include(r => r.Contract).SingleOrDefaultAsync(r => r.Id == id);
        if (request is null || !user.CanViewClient(request.Contract!.ClientId))
        {
            return Results.NotFound();
        }
        if (!user.CanManageClient(request.Contract!.ClientId))
        {
            return Results.Problem(
                detail: "Only platform admins or the client's own admins can decide leave requests.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            transition(request);
        }
        catch (DomainException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync();
        return Results.Ok(ToResponse(request));
    }

    private static LeavePolicyResponse ToResponse(LeavePolicy p) =>
        new(p.Id, p.CountryCode, p.AnnualLeaveDays, p.SickLeaveDays, p.CreatedAtUtc);

    private static LeaveRequestResponse ToResponse(LeaveRequest r) => new(
        r.Id,
        r.ContractId,
        r.Type.ToString(),
        r.StartDate,
        r.EndDate,
        r.Days,
        r.Reason,
        r.Status.ToString(),
        r.RequestedAtUtc,
        r.DecidedAtUtc,
        r.DecisionNote);
}

public record CreateLeavePolicyRequest(string? CountryCode, int AnnualLeaveDays, int SickLeaveDays);

public record LeavePolicyResponse(
    Guid Id,
    string CountryCode,
    int AnnualLeaveDays,
    int SickLeaveDays,
    DateTimeOffset CreatedAtUtc);

public record CreateLeaveRequestRequest(
    Guid ContractId,
    string? Type,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Reason);

public record DecideLeaveRequestRequest(string? Note);

public record LeaveRequestResponse(
    Guid Id,
    Guid ContractId,
    string Type,
    DateOnly StartDate,
    DateOnly EndDate,
    int Days,
    string? Reason,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote);

public record LeaveBalanceResponse(
    string Type,
    int AllowanceDays,
    int ApprovedDays,
    int PendingDays,
    int RemainingDays);

public record ContractLeaveBalancesResponse(
    Guid ContractId,
    int Year,
    List<LeaveBalanceResponse> Balances);
