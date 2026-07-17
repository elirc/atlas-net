namespace Atlas.Domain.Entities;

public enum ApiRole
{
    /// <summary>Operates the Atlas platform itself: full access to every client and country.</summary>
    PlatformAdmin = 0,

    /// <summary>Manages one client's workforce: read plus writes scoped to that client.</summary>
    ClientAdmin = 1,

    /// <summary>Read-only access scoped to one client.</summary>
    ClientViewer = 2,
}

/// <summary>
/// An API credential. Platform admins are unscoped; client roles are bound to a
/// single client and can only see (and, for admins, change) that client's data.
/// </summary>
public class ApiUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable owner of the key, e.g. "Acme Robotics admin".</summary>
    public required string Name { get; set; }

    /// <summary>The secret presented in the X-Api-Key header. Unique.</summary>
    public required string ApiKey { get; set; }

    public required ApiRole Role { get; set; }

    /// <summary>The client this key is scoped to; null for platform admins.</summary>
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Deactivated keys fail authentication; deactivation is the revocation mechanism.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Client-scoped roles must carry a ClientId; platform admins must not.</summary>
    public static bool RoleRequiresClient(ApiRole role) => role != ApiRole.PlatformAdmin;
}
