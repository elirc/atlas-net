using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atlas.Tests.Integration;

public class ProductionReadinessTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ProductionReadinessTests(AtlasApiFactory factory)
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
            }
            if (!db.LeavePolicies.Any(p => p.CountryCode == "PH"))
            {
                db.LeavePolicies.Add(new LeavePolicy { CountryCode = "PH", AnnualLeaveDays = 15, SickLeaveDays = 10 });
            }
            db.SaveChanges();
        });
    }

    [Fact]
    public async Task Health_IncludesDatabaseProbe()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("healthy", payload!.Status);
        Assert.Equal("atlas-net", payload.Service);
        var database = Assert.Single(payload.Checks, c => c.Name == "database");
        Assert.Equal("Healthy", database.Status);
    }

    [Fact]
    public async Task ListEndpoints_PaginateWithHeaders()
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/workers", new
            {
                fullName = $"Paged Worker {i}",
                email = $"paged{i}.worker@example.com",
                countryCode = "PH",
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var pageOne = await _client.GetAsync("/api/workers?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, pageOne.StatusCode);
        var firstPage = await pageOne.Content.ReadFromJsonAsync<List<WorkerResponse>>();
        Assert.Equal(2, firstPage!.Count);
        Assert.True(int.Parse(pageOne.Headers.GetValues("X-Total-Count").Single()) >= 5);
        Assert.Equal("1", pageOne.Headers.GetValues("X-Page").Single());
        Assert.Equal("2", pageOne.Headers.GetValues("X-Page-Size").Single());

        var pageTwo = await _client.GetAsync("/api/workers?page=2&pageSize=2");
        var secondPage = await pageTwo.Content.ReadFromJsonAsync<List<WorkerResponse>>();
        Assert.Equal(2, secondPage!.Count);
        Assert.Empty(firstPage.Select(w => w.Id).Intersect(secondPage.Select(w => w.Id)));
    }

    [Theory]
    [InlineData("/api/workers?page=0")]
    [InlineData("/api/workers?pageSize=0")]
    [InlineData("/api/workers?pageSize=500")]
    [InlineData("/api/contracts?page=-1")]
    [InlineData("/api/invoices?pageSize=201")]
    public async Task InvalidPagination_ReturnsValidationProblem(string url)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DuplicateConflicts_UseProblemDetailsShape()
    {
        var first = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Conflict Worker",
            email = "conflict.worker@example.com",
            countryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Conflict Worker II",
            email = "conflict.worker@example.com",
            countryCode = "PH",
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
        var problem = await duplicate.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal(409, problem!.Status);
        Assert.Contains("already exists", problem.Detail);
    }

    [Fact]
    public async Task ConcurrentUpdates_FailWithStaleVersion()
    {
        // An active contract with a pending leave request.
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Race Co",
            billingEmail = "race@example.com",
            headquartersCountryCode = "PH",
        });
        var clientCompany = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Race Worker",
            email = "race.worker@example.com",
            countryCode = "PH",
        });
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();
        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = clientCompany!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });
        var contract = await contractResponse.Content.ReadFromJsonAsync<ContractResponse>();
        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired && !i.IsCompleted))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });
        await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);
        var leaveResponse = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-08-03",
            endDate = "2026-08-07",
        });
        Assert.Equal(HttpStatusCode.Created, leaveResponse.StatusCode);
        var leave = await leaveResponse.Content.ReadFromJsonAsync<LeaveRequestResponse>();

        // Two contexts load the same pending request; the second save is stale.
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var requestA = await dbA.LeaveRequests.SingleAsync(r => r.Id == leave!.Id);
        var requestB = await dbB.LeaveRequests.SingleAsync(r => r.Id == leave!.Id);

        requestA.Approve(DateTimeOffset.UtcNow);
        await dbA.SaveChangesAsync();

        requestB.Cancel(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        // The winner's decision stands.
        var final = await _client.GetFromJsonAsync<LeaveRequestResponse>($"/api/leave-requests/{leave!.Id}");
        Assert.Equal("Approved", final!.Status);
    }

    [Fact]
    public async Task Requests_AreLogged_WithMethodPathAndStatus()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = new AtlasApiFactory();
        var client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(capture))).CreateClient();

        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(capture.Messages, m =>
            m.Category.Contains("RequestLoggingMiddleware") && m.Message.Contains("HTTP GET /health responded 200"));
    }

    private sealed record HealthCheckEntry(string Name, string Status, string? Description, double DurationMs);

    private sealed record HealthResponse(string Status, string Service, DateTime TimestampUtc, List<HealthCheckEntry> Checks);

    private sealed record ProblemBody(string? Title, int Status, string Detail);

    /// <summary>Captures log messages so middleware logging can be asserted.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(string Category, string Message)> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentBag<(string, string)> _messages;

            public CapturingLogger(string category, ConcurrentBag<(string, string)> messages)
            {
                _category = category;
                _messages = messages;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _messages.Add((_category, formatter(state, exception)));
        }
    }
}
