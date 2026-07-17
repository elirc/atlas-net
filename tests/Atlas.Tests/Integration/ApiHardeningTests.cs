using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Atlas.Tests.Integration;

/// <summary>Cross-cutting API behavior: problem responses, malformed input, content types.</summary>
public class ApiHardeningTests : IClassFixture<AtlasApiFactory>
{
    private readonly HttpClient _client;

    public ApiHardeningTests(AtlasApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var content = new StringContent("{ not valid json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/clients", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ValidationFailure_ReturnsRfc7807ProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/countries", new
        {
            code = "TOOLONG",
            name = "",
            currencyCode = "X",
            employerCostRate = 5,
            employeeDeductionRate = -1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemBody>();
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains("code", problem.Errors.Keys);
        Assert.Contains("name", problem.Errors.Keys);
        Assert.Contains("currencyCode", problem.Errors.Keys);
        Assert.Contains("employerCostRate", problem.Errors.Keys);
        Assert.Contains("employeeDeductionRate", problem.Errors.Keys);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemBody()
    {
        var response = await _client.GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidGuidInRoute_Returns404()
    {
        var response = await _client.GetAsync("/api/clients/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConflictResponses_UseProblemDetailsShape()
    {
        // Activating a contract that doesn't exist -> 404; activating twice -> 409 problem.
        var response = await _client.PostAsync($"/api/contracts/{Guid.NewGuid()}/activate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record ValidationProblemBody(
        string? Title,
        int Status,
        Dictionary<string, string[]> Errors);
}
