using Atlas.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atlas.Api;

/// <summary>Probes the database with a connectivity check.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly AtlasDbContext _db;

    public DatabaseHealthCheck(AtlasDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Cannot connect to the database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database probe failed.", ex);
        }
    }
}

/// <summary>Writes the health report in the service's JSON shape.</summary>
public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status switch
            {
                HealthStatus.Healthy => "healthy",
                HealthStatus.Degraded => "degraded",
                _ => "unhealthy",
            },
            service = "atlas-net",
            timestampUtc = DateTime.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            }),
        });
    }
}
