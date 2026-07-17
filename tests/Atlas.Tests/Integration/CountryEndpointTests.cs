using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;

namespace Atlas.Tests.Integration;

public class CountryEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public CountryEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateCountry_ThenFetchIt_RoundTrips()
    {
        var response = await _client.PostAsJsonAsync("/api/countries", new
        {
            code = "nl",
            name = "Netherlands",
            currencyCode = "eur",
            employerCostRate = 0.18,
            employeeDeductionRate = 0.27,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CountryResponse>();
        Assert.NotNull(created);
        Assert.Equal("NL", created.Code);
        Assert.Equal("EUR", created.CurrencyCode);
        Assert.Equal(0.18m, created.EmployerCostRate);

        var fetched = await _client.GetFromJsonAsync<CountryResponse>("/api/countries/NL");
        Assert.NotNull(fetched);
        Assert.Equal("Netherlands", fetched.Name);
    }

    [Fact]
    public async Task CreateCountry_DuplicateCode_ReturnsConflict()
    {
        var payload = new
        {
            code = "SE",
            name = "Sweden",
            currencyCode = "SEK",
            employerCostRate = 0.31,
            employeeDeductionRate = 0.30,
        };

        var first = await _client.PostAsJsonAsync("/api/countries", payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/countries", payload);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("X", "Xland", "XXX", 0.1, 0.1)] // bad code length
    [InlineData("XX", "", "XXX", 0.1, 0.1)] // missing name
    [InlineData("XX", "Xland", "XXXX", 0.1, 0.1)] // bad currency length
    [InlineData("XX", "Xland", "XXX", 1.5, 0.1)] // employer rate out of range
    [InlineData("XX", "Xland", "XXX", 0.1, -0.2)] // negative deduction rate
    public async Task CreateCountry_InvalidPayload_ReturnsValidationProblem(
        string code, string name, string currency, double employerRate, double deductionRate)
    {
        var response = await _client.PostAsJsonAsync("/api/countries", new
        {
            code,
            name,
            currencyCode = currency,
            employerCostRate = employerRate,
            employeeDeductionRate = deductionRate,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCountry_UnknownCode_Returns404()
    {
        var response = await _client.GetAsync("/api/countries/ZZ");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
