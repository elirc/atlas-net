using System.Security.Claims;
using System.Text.Encodings.Web;
using Atlas.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Api.Auth;

/// <summary>
/// Authenticates requests by looking up the X-Api-Key header against active
/// <see cref="Atlas.Domain.Entities.ApiUser"/> rows. Successful authentication
/// yields a principal carrying the user's role and (for client roles) client id.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = header.ToString();
        var db = Context.RequestServices.GetRequiredService<AtlasDbContext>();
        var user = await db.ApiUsers.SingleOrDefaultAsync(u => u.ApiKey == apiKey);
        if (user is null || !user.IsActive)
        {
            return AuthenticateResult.Fail("Unknown or inactive API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        if (user.ClientId is not null)
        {
            claims.Add(new Claim(AtlasClaimTypes.ClientId, user.ClientId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
