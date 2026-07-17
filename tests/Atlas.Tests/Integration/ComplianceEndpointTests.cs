using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class ComplianceEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ComplianceEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.WithDb(db =>
        {
            if (!db.Countries.Any(c => c.Code == "PH"))
            {
                db.Countries.Add(new Country
                {
                    Code = "PH",
                    Name = "Philippines",
                    CurrencyCode = "PHP",
                    EmployerCostRate = 0.12m,
                    EmployeeDeductionRate = 0.15m,
                });
                db.SaveChanges();
            }
        });
    }

    private async Task<WorkerResponse> CreateWorkerAsync(string prefix)
    {
        var response = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{prefix} Worker",
            email = $"{prefix}.worker@example.com",
            countryCode = "PH",
        });
        return (await response.Content.ReadFromJsonAsync<WorkerResponse>())!;
    }

    [Fact]
    public async Task AddDocument_ThenListIt_RoundTripsWithStatus()
    {
        var worker = await CreateWorkerAsync("docadd");
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(5);

        var response = await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
        {
            type = "passport",
            name = "PH Passport",
            issuedDate = "2022-01-15",
            expiryDate = expiry.ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ComplianceDocumentResponse>();
        Assert.Equal("Passport", created!.Type);
        Assert.Equal("Valid", created.Status);

        var documents = await _client.GetFromJsonAsync<List<ComplianceDocumentResponse>>(
            $"/api/workers/{worker.Id}/documents");
        Assert.Single(documents!);
        Assert.Equal("PH Passport", documents![0].Name);
    }

    [Fact]
    public async Task AddDocument_ExpiryBeforeIssued_ReturnsValidationProblem()
    {
        var worker = await CreateWorkerAsync("docbaddates");

        var response = await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
        {
            type = "Visa",
            name = "Backwards visa",
            issuedDate = "2026-05-01",
            expiryDate = "2026-04-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddDocument_UnknownType_ReturnsValidationProblem()
    {
        var worker = await CreateWorkerAsync("docbadtype");

        var response = await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
        {
            type = "LibraryCard",
            name = "Not a compliance doc",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddDocument_UnknownWorker_Returns404()
    {
        var response = await _client.PostAsJsonAsync($"/api/workers/{Guid.NewGuid()}/documents", new
        {
            type = "Passport",
            name = "Ghost passport",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExpiringReport_ReturnsOnlyDocumentsInsideWindow_SortedByExpiry()
    {
        var worker = await CreateWorkerAsync("docexpiring");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task AddDoc(string name, DateOnly? expiry) =>
            await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
            {
                type = "Visa",
                name,
                expiryDate = expiry?.ToString("yyyy-MM-dd"),
            });

        await AddDoc("expired-doc", today.AddDays(-5));
        await AddDoc("soon-doc", today.AddDays(10));
        await AddDoc("later-doc", today.AddDays(200));
        await AddDoc("forever-doc", null);

        var report = await _client.GetFromJsonAsync<List<ExpiringDocumentResponse>>(
            "/api/compliance/expiring?withinDays=30");

        Assert.NotNull(report);
        var mine = report.Where(d => d.WorkerId == worker.Id).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal("expired-doc", mine[0].Name);
        Assert.Equal("Expired", mine[0].Status);
        Assert.Equal("soon-doc", mine[1].Name);
        Assert.Equal("ExpiringSoon", mine[1].Status);
        Assert.Equal($"{worker.FullName}", mine[0].WorkerName);
    }

    [Fact]
    public async Task ExpiringReport_NegativeWindow_ReturnsValidationProblem()
    {
        var response = await _client.GetAsync("/api/compliance/expiring?withinDays=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
