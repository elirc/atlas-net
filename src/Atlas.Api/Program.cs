using Atlas.Api.Endpoints;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<PayrollService>();

var app = builder.Build();

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
