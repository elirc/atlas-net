using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class ExpenseEndpoints
{
    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/expense-claims").RequireAuthorization();

        group.MapGet("/", async (Guid? contractId, string? status, int? page, int? pageSize, ClaimsPrincipal user, HttpContext http, AtlasDbContext db) =>
        {
            if (Pagination.Validate(page, pageSize, out var paging) is { } problem)
            {
                return problem;
            }

            var query = db.ExpenseClaims.Include(e => e.Items).AsQueryable();
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
                if (!Enum.TryParse<ExpenseClaimStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{status}'. Expected {string.Join(", ", Enum.GetNames<ExpenseClaimStatus>())}."],
                    });
                }
                query = query.Where(e => e.Status == parsed);
            }

            var claims = await query.OrderBy(e => e.SubmittedAtUtc).ToPageAsync(http, paging);
            return Results.Ok(claims.Select(ToResponse).ToList());
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var claim = await db.ExpenseClaims
                .Include(e => e.Items)
                .Include(e => e.Contract)
                .SingleOrDefaultAsync(e => e.Id == id);
            return claim is null || !user.CanViewClient(claim.Contract!.ClientId)
                ? Results.NotFound()
                : Results.Ok(ToResponse(claim));
        });

        group.MapPost("/", async (CreateExpenseClaimRequest request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ContractId == Guid.Empty)
            {
                errors["contractId"] = ["ContractId is required."];
            }
            if (request.Items is null || request.Items.Count == 0)
            {
                errors["items"] = ["At least one expense item is required."];
            }
            else
            {
                for (var i = 0; i < request.Items.Count; i++)
                {
                    var item = request.Items[i];
                    if (string.IsNullOrWhiteSpace(item.Description))
                    {
                        errors[$"items[{i}].description"] = ["Description is required."];
                    }
                    if (item.Amount <= 0)
                    {
                        errors[$"items[{i}].amount"] = ["Amount must be greater than zero."];
                    }
                    if (item.IncurredDate == default)
                    {
                        errors[$"items[{i}].incurredDate"] = ["IncurredDate is required."];
                    }
                    if (!string.IsNullOrWhiteSpace(item.ReceiptUrl)
                        && (!Uri.TryCreate(item.ReceiptUrl, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                    {
                        errors[$"items[{i}].receiptUrl"] = ["ReceiptUrl must be an absolute http(s) URL."];
                    }
                }
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
                    detail: "Only platform admins or the client's own admins can submit expense claims.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (contract.Status != ContractStatus.Active)
            {
                return Results.Problem(
                    detail: $"Expenses can only be claimed on active contracts; this contract is {contract.Status}.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var claim = new ExpenseClaim
            {
                ContractId = contract.Id,
                CurrencyCode = contract.CurrencyCode,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            };
            claim.Items.AddRange(request.Items!.Select(i => new ExpenseItem
            {
                ExpenseClaimId = claim.Id,
                Description = i.Description!.Trim(),
                Amount = i.Amount,
                IncurredDate = i.IncurredDate,
                ReceiptUrl = string.IsNullOrWhiteSpace(i.ReceiptUrl) ? null : i.ReceiptUrl.Trim(),
            }));
            db.ExpenseClaims.Add(claim);
            await db.SaveChangesAsync();

            return Results.Created($"/api/expense-claims/{claim.Id}", ToResponse(claim));
        });

        group.MapPost("/{id:guid}/approve", async (
            Guid id, DecideExpenseClaimRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
                await DecideAsync(id, user, db, c => c.Approve(DateTimeOffset.UtcNow, request?.Note)));

        group.MapPost("/{id:guid}/reject", async (
            Guid id, DecideExpenseClaimRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
                await DecideAsync(id, user, db, c => c.Reject(request?.Note ?? string.Empty, DateTimeOffset.UtcNow)));

        return app;
    }

    /// <summary>Shared load + authorize + transition + save pipeline for claim decisions.</summary>
    private static async Task<IResult> DecideAsync(
        Guid id, ClaimsPrincipal user, AtlasDbContext db, Action<ExpenseClaim> transition)
    {
        var claim = await db.ExpenseClaims
            .Include(e => e.Items)
            .Include(e => e.Contract)
            .SingleOrDefaultAsync(e => e.Id == id);
        if (claim is null || !user.CanViewClient(claim.Contract!.ClientId))
        {
            return Results.NotFound();
        }
        if (!user.CanManageClient(claim.Contract!.ClientId))
        {
            return Results.Problem(
                detail: "Only platform admins or the client's own admins can decide expense claims.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            transition(claim);
        }
        catch (DomainException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync();
        return Results.Ok(ToResponse(claim));
    }

    internal static ExpenseClaimResponse ToResponse(ExpenseClaim e) => new(
        e.Id,
        e.ContractId,
        e.CurrencyCode,
        e.Description,
        e.Status.ToString(),
        e.TotalAmount,
        e.SubmittedAtUtc,
        e.DecidedAtUtc,
        e.DecisionNote,
        e.ReimbursedInPayrollRunId,
        e.ReimbursedAtUtc,
        e.Items
            .OrderBy(i => i.IncurredDate)
            .Select(i => new ExpenseItemResponse(i.Id, i.Description, i.Amount, i.IncurredDate, i.ReceiptUrl))
            .ToList());
}

public record CreateExpenseItemRequest(
    string? Description,
    decimal Amount,
    DateOnly IncurredDate,
    string? ReceiptUrl);

public record CreateExpenseClaimRequest(
    Guid ContractId,
    string? Description,
    List<CreateExpenseItemRequest>? Items);

public record DecideExpenseClaimRequest(string? Note);

public record ExpenseItemResponse(
    Guid Id,
    string Description,
    decimal Amount,
    DateOnly IncurredDate,
    string? ReceiptUrl);

public record ExpenseClaimResponse(
    Guid Id,
    Guid ContractId,
    string CurrencyCode,
    string? Description,
    string Status,
    decimal TotalAmount,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    Guid? ReimbursedInPayrollRunId,
    DateTimeOffset? ReimbursedAtUtc,
    List<ExpenseItemResponse> Items);
