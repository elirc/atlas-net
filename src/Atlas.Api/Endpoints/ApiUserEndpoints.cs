using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

public static class ApiUserEndpoints
{
    public static IEndpointRouteBuilder MapApiUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/api-users").RequireAuthorization(AuthPolicies.PlatformAdmin);

        group.MapGet("/", async (AtlasDbContext db) =>
        {
            var users = await db.ApiUsers.OrderBy(u => u.CreatedAtUtc).ToListAsync();
            return Results.Ok(users.Select(ToResponse).ToList());
        });

        group.MapPost("/", async (CreateApiUserRequest request, AtlasDbContext db) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }
            ApiRole role = default;
            if (string.IsNullOrWhiteSpace(request.Role)
                || !Enum.TryParse(request.Role, ignoreCase: true, out role))
            {
                errors["role"] = [$"Role must be one of: {string.Join(", ", Enum.GetNames<ApiRole>())}."];
            }
            else if (ApiUser.RoleRequiresClient(role) && (request.ClientId is null || request.ClientId == Guid.Empty))
            {
                errors["clientId"] = [$"ClientId is required for the {role} role."];
            }
            else if (!ApiUser.RoleRequiresClient(role) && request.ClientId is not null)
            {
                errors["clientId"] = ["ClientId must not be set for platform admins."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (request.ClientId is not null && await db.Clients.FindAsync(request.ClientId) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = [$"Client '{request.ClientId}' does not exist."],
                });
            }

            var user = new ApiUser
            {
                Name = request.Name!.Trim(),
                ApiKey = $"atlas_{Guid.NewGuid():N}",
                Role = role,
                ClientId = request.ClientId,
            };
            db.ApiUsers.Add(user);
            await db.SaveChangesAsync();

            // The full key is returned exactly once, on creation.
            return Results.Created($"/api/api-users/{user.Id}", new ApiUserCreatedResponse(
                user.Id, user.Name, user.Role.ToString(), user.ClientId, user.IsActive, user.CreatedAtUtc, user.ApiKey));
        });

        group.MapPost("/{id:guid}/deactivate", async (Guid id, AtlasDbContext db) =>
        {
            var user = await db.ApiUsers.FindAsync(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.IsActive = false;
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(user));
        });

        return app;
    }

    private static ApiUserResponse ToResponse(ApiUser u) =>
        new(u.Id, u.Name, u.Role.ToString(), u.ClientId, u.IsActive, u.CreatedAtUtc, MaskKey(u.ApiKey));

    /// <summary>Keys are only listed masked; the full secret is shown once on creation.</summary>
    private static string MaskKey(string key) =>
        key.Length <= 4 ? "****" : $"****{key[^4..]}";
}

public record CreateApiUserRequest(string? Name, string? Role, Guid? ClientId);

public record ApiUserResponse(
    Guid Id,
    string Name,
    string Role,
    Guid? ClientId,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    string ApiKeyMasked);

public record ApiUserCreatedResponse(
    Guid Id,
    string Name,
    string Role,
    Guid? ClientId,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    string ApiKey);
