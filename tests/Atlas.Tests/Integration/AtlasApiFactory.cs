using Atlas.Api.Auth;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests.Integration;

/// <summary>
/// Boots the API against an isolated in-memory SQLite database (one per factory instance).
/// The connection is held open for the factory's lifetime so the schema survives.
/// A platform-admin API key is seeded and attached to every client by default;
/// use <see cref="CreateClientWithApiKey"/> for anonymous or scoped clients.
/// </summary>
public class AtlasApiFactory : WebApplicationFactory<Program>
{
    public const string AdminApiKey = "test-platform-admin-key";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AtlasDbContext>)
                            || d.ServiceType == typeof(AtlasDbContext))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            _connection.Open();
            services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
            db.Database.EnsureCreated();
            if (!db.ApiUsers.Any(u => u.ApiKey == AdminApiKey))
            {
                db.ApiUsers.Add(new ApiUser
                {
                    Name = "Test platform admin",
                    ApiKey = AdminApiKey,
                    Role = ApiRole.PlatformAdmin,
                });
                db.SaveChanges();
            }
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, AdminApiKey);
    }

    /// <summary>Creates an HttpClient using the given API key, or no key at all when null.</summary>
    public HttpClient CreateClientWithApiKey(string? apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthenticationHandler.HeaderName);
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, apiKey);
        }

        return client;
    }

    /// <summary>Runs an action against a fresh DbContext scope.</summary>
    public void WithDb(Action<AtlasDbContext> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        action(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}
