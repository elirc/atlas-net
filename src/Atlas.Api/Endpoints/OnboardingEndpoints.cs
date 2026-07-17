using System.Security.Claims;
using Atlas.Api.Auth;
using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{contractId:guid}/onboarding").RequireAuthorization();

        group.MapGet("/", async (Guid contractId, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var contract = await db.Contracts.FindAsync(contractId);
            if (contract is null || !user.CanViewClient(contract.ClientId))
            {
                return Results.NotFound();
            }

            var items = await db.OnboardingItems
                .Where(i => i.ContractId == contractId)
                .OrderBy(i => i.Type)
                .ToListAsync();

            return Results.Ok(new OnboardingChecklistResponse(
                contractId,
                items.All(i => !i.IsRequired || i.IsCompleted),
                items.Select(ToResponse).ToList()));
        });

        group.MapPost("/{itemId:guid}/complete", async (
            Guid contractId, Guid itemId, CompleteOnboardingItemRequest? request, ClaimsPrincipal user, AtlasDbContext db) =>
        {
            var item = await db.OnboardingItems
                .Include(i => i.Contract)
                .SingleOrDefaultAsync(i => i.Id == itemId && i.ContractId == contractId);
            if (item is null || !user.CanViewClient(item.Contract!.ClientId))
            {
                return Results.NotFound();
            }
            if (!user.CanManageClient(item.Contract!.ClientId))
            {
                return Results.Problem(
                    detail: "Only platform admins or the client's own admins can complete onboarding items.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                item.Complete(DateTimeOffset.UtcNow, request?.Notes);
            }
            catch (DomainException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(item));
        });

        return app;
    }

    private static OnboardingItemResponse ToResponse(OnboardingItem i) =>
        new(i.Id, i.ContractId, i.Type.ToString(), i.Title, i.IsRequired, i.IsCompleted, i.CompletedAtUtc, i.Notes);
}

public record CompleteOnboardingItemRequest(string? Notes);

public record OnboardingItemResponse(
    Guid Id,
    Guid ContractId,
    string Type,
    string Title,
    bool IsRequired,
    bool IsCompleted,
    DateTimeOffset? CompletedAtUtc,
    string? Notes);

public record OnboardingChecklistResponse(
    Guid ContractId,
    bool IsComplete,
    List<OnboardingItemResponse> Items);
