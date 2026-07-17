using Atlas.Api;
using Atlas.Api.Auth;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<PayrollService>();
builder.Services.AddScoped<LeaveService>();

// API-key authentication (X-Api-Key header) with role/client-scope claims.
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AuthPolicies.PlatformAdmin, policy => policy.RequireRole(nameof(ApiRole.PlatformAdmin))));

// RFC 7807 problem responses for unhandled errors and bare status codes.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
    db.Database.EnsureCreated();
    DataSeeder.Seed(db);
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "atlas-net",
    timestampUtc = DateTime.UtcNow,
}));

app.MapCountryEndpoints();
app.MapClientEndpoints();
app.MapWorkerEndpoints();
app.MapContractEndpoints();
app.MapOnboardingEndpoints();
app.MapComplianceEndpoints();
app.MapPayrollEndpoints();
app.MapInvoiceEndpoints();
app.MapApiUserEndpoints();
app.MapLeaveEndpoints();
app.MapExpenseEndpoints();
app.MapAmendmentEndpoints();
app.MapFxRateEndpoints();
app.MapBenefitEndpoints();

app.Run();

/// <summary>Exposes the implicit Program class to WebApplicationFactory-based integration tests.</summary>
public partial class Program;
