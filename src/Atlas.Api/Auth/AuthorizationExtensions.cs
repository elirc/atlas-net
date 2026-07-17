using System.Security.Claims;
using Atlas.Domain.Entities;

namespace Atlas.Api.Auth;

public static class AtlasClaimTypes
{
    /// <summary>The client a client-scoped API user belongs to.</summary>
    public const string ClientId = "atlas:client_id";
}

public static class AuthPolicies
{
    /// <summary>Endpoints that operate the platform itself (countries, workers, payroll, keys).</summary>
    public const string PlatformAdmin = nameof(ApiRole.PlatformAdmin);
}

/// <summary>
/// Answers "who is calling and which client are they scoped to". Cross-client
/// reads should 404 (do not reveal existence); insufficient-role writes should 403.
/// </summary>
public static class AuthorizationExtensions
{
    public static bool IsPlatformAdmin(this ClaimsPrincipal user) =>
        user.IsInRole(nameof(ApiRole.PlatformAdmin));

    /// <summary>The client this caller is scoped to; null for platform admins.</summary>
    public static Guid? ClientIdOrNull(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AtlasClaimTypes.ClientId), out var id) ? id : null;

    /// <summary>True when the caller may read data belonging to <paramref name="clientId"/>.</summary>
    public static bool CanViewClient(this ClaimsPrincipal user, Guid clientId) =>
        user.IsPlatformAdmin() || user.ClientIdOrNull() == clientId;

    /// <summary>
    /// True when the caller may change data belonging to <paramref name="clientId"/>:
    /// platform admins and that client's own admins.
    /// </summary>
    public static bool CanManageClient(this ClaimsPrincipal user, Guid clientId) =>
        user.IsPlatformAdmin()
        || (user.IsInRole(nameof(ApiRole.ClientAdmin)) && user.ClientIdOrNull() == clientId);
}
