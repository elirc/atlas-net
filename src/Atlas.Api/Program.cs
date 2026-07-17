using Atlas.Api;
using Atlas.Api.Endpoints;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<PayrollService>();

// RFC 7807 problem responses for unhandled errors and bare status codes.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

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

app.Run();

/// <summary>Exposes the implicit Program class to WebApplicationFactory-based integration tests.</summary>
public partial class Program;
