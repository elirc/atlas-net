using System.Net;
using System.Net.Http.Json;

namespace Atlas.Tests.Integration;

public class HealthEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(AtlasApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsHealthyStatusAndServiceName()
    {
        var payload = await _client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(payload);
        Assert.Equal("healthy", payload.Status);
        Assert.Equal("atlas-net", payload.Service);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await _client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record HealthResponse(string Status, string Service, DateTime TimestampUtc);
}
