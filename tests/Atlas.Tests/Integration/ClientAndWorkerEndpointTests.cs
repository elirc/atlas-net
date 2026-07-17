using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class ClientAndWorkerEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ClientAndWorkerEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        EnsureCountry("US", "United States", "USD");
        EnsureCountry("PH", "Philippines", "PHP");
    }

    private void EnsureCountry(string code, string name, string currency) =>
        _factory.WithDb(db =>
        {
            if (!db.Countries.Any(c => c.Code == code))
            {
                db.Countries.Add(new Country
                {
                    Code = code,
                    Name = name,
                    CurrencyCode = currency,
                    EmployerCostRate = 0.10m,
                    EmployeeDeductionRate = 0.20m,
                });
                db.SaveChanges();
            }
        });

    [Fact]
    public async Task CreateClient_ThenFetchIt_RoundTrips()
    {
        var response = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Globex",
            legalName = "Globex Corporation",
            billingEmail = "ap@globex.example",
            headquartersCountryCode = "us",
            managementFeeRate = 0.12,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ClientResponse>();
        Assert.NotNull(created);
        Assert.Equal("US", created.HeadquartersCountryCode);
        Assert.Equal(0.12m, created.ManagementFeeRate);

        var fetched = await _client.GetFromJsonAsync<ClientResponse>($"/api/clients/{created.Id}");
        Assert.NotNull(fetched);
        Assert.Equal("Globex", fetched.Name);
    }

    [Fact]
    public async Task CreateClient_UnsupportedCountry_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Initech",
            billingEmail = "ap@initech.example",
            headquartersCountryCode = "ZZ",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body);
    }

    [Fact]
    public async Task CreateWorker_NormalizesEmailAndCountry()
    {
        var response = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Ana Reyes",
            email = "Ana.Reyes@Example.com",
            countryCode = "ph",
            dateOfBirth = "1995-06-15",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkerResponse>();
        Assert.NotNull(created);
        Assert.Equal("ana.reyes@example.com", created.Email);
        Assert.Equal("PH", created.CountryCode);
        Assert.Equal(new DateOnly(1995, 6, 15), created.DateOfBirth);
    }

    [Fact]
    public async Task CreateWorker_DuplicateEmail_ReturnsConflict()
    {
        var payload = new
        {
            fullName = "Ben Cruz",
            email = "ben.cruz@example.com",
            countryCode = "PH",
        };

        var first = await _client.PostAsJsonAsync("/api/workers", payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/workers", payload);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ListWorkers_FilterByCountry_ReturnsOnlyThatCountry()
    {
        await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "US Worker",
            email = "us.worker@example.com",
            countryCode = "US",
        });
        await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "PH Worker",
            email = "ph.worker@example.com",
            countryCode = "PH",
        });

        var phWorkers = await _client.GetFromJsonAsync<List<WorkerResponse>>("/api/workers?countryCode=ph");

        Assert.NotNull(phWorkers);
        Assert.NotEmpty(phWorkers);
        Assert.All(phWorkers, w => Assert.Equal("PH", w.CountryCode));
    }
}
