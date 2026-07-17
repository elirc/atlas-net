using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class ContractEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ContractEndpointTests(AtlasApiFactory factory)
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

    private async Task<(Guid ClientId, Guid WorkerId)> CreateClientAndWorkerAsync(string emailPrefix)
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{emailPrefix} Co",
            billingEmail = $"{emailPrefix}@example.com",
            headquartersCountryCode = "PH",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();

        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{emailPrefix} Worker",
            email = $"{emailPrefix}.worker@example.com",
            countryCode = "PH",
        });
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        return (client!.Id, worker!.Id);
    }

    /// <summary>Completes every required onboarding item so the contract can be activated.</summary>
    private void CompleteRequiredOnboarding(Guid contractId) =>
        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contractId && i.IsRequired && !i.IsCompleted))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });

    private async Task<HttpResponseMessage> ActivateAsync(Guid contractId)
    {
        CompleteRequiredOnboarding(contractId);
        return await _client.PostAsync($"/api/contracts/{contractId}/activate", null);
    }

    private async Task<ContractResponse> CreateDraftContractAsync(Guid clientId, Guid workerId)
    {
        var response = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 120000,
            startDate = "2026-08-01",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContractResponse>())!;
    }

    [Fact]
    public async Task CreateContract_DefaultsToDraft_AndInheritsCountryCurrencyFromWorker()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("draftco");

        var contract = await CreateDraftContractAsync(clientId, workerId);

        Assert.Equal("Draft", contract.Status);
        Assert.Equal("PH", contract.CountryCode);
        Assert.Equal("PHP", contract.CurrencyCode);
        Assert.Equal(120000m, contract.MonthlySalary);
        Assert.Null(contract.ActivatedAtUtc);
    }

    [Fact]
    public async Task CreateContract_UnknownWorker_ReturnsValidationProblem()
    {
        var (clientId, _) = await CreateClientAndWorkerAsync("noworker");

        var response = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId = Guid.NewGuid(),
            jobTitle = "Engineer",
            monthlySalary = 120000,
            startDate = "2026-08-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_ZeroSalary_ReturnsValidationProblem()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("zerosalary");

        var response = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 0,
            startDate = "2026-08-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_WorkerWithOpenContract_ReturnsConflict()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("doublehire");
        await CreateDraftContractAsync(clientId, workerId);

        var response = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Second Job",
            monthlySalary = 90000,
            startDate = "2026-09-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ActivateContract_Draft_BecomesActive()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("activate");
        var contract = await CreateDraftContractAsync(clientId, workerId);

        var response = await ActivateAsync(contract.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activated = await response.Content.ReadFromJsonAsync<ContractResponse>();
        Assert.Equal("Active", activated!.Status);
        Assert.NotNull(activated.ActivatedAtUtc);
    }

    [Fact]
    public async Task ActivateContract_Twice_ReturnsConflict()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("reactivate");
        var contract = await CreateDraftContractAsync(clientId, workerId);
        await ActivateAsync(contract.Id);

        var second = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task TerminateContract_ActiveWithValidEndDate_BecomesTerminated()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("terminate");
        var contract = await CreateDraftContractAsync(clientId, workerId);
        await ActivateAsync(contract.Id);

        var response = await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-12-31",
            reason = "End of engagement",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var terminated = await response.Content.ReadFromJsonAsync<ContractResponse>();
        Assert.Equal("Terminated", terminated!.Status);
        Assert.Equal(new DateOnly(2026, 12, 31), terminated.EndDate);
        Assert.Equal("End of engagement", terminated.TerminationReason);
    }

    [Fact]
    public async Task TerminateContract_Draft_ReturnsConflict()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("termdraft");
        var contract = await CreateDraftContractAsync(clientId, workerId);

        var response = await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-12-31",
            reason = "Too early",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TerminateContract_EndDateBeforeStart_ReturnsConflict()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("badend");
        var contract = await CreateDraftContractAsync(clientId, workerId);
        await ActivateAsync(contract.Id);

        var response = await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-07-01", // contract starts 2026-08-01
            reason = "Backdated",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TerminatedWorker_CanBeRehired()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("rehire");
        var contract = await CreateDraftContractAsync(clientId, workerId);
        await ActivateAsync(contract.Id);
        await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-12-31",
            reason = "Project ended",
        });

        var rehire = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer II",
            monthlySalary = 140000,
            startDate = "2027-02-01",
        });

        Assert.Equal(HttpStatusCode.Created, rehire.StatusCode);
    }

    [Fact]
    public async Task ListContracts_FilterByStatus_ReturnsMatchesOnly()
    {
        var (clientId, workerId) = await CreateClientAndWorkerAsync("filterstatus");
        var contract = await CreateDraftContractAsync(clientId, workerId);
        await ActivateAsync(contract.Id);

        var active = await _client.GetFromJsonAsync<List<ContractResponse>>("/api/contracts?status=active");

        Assert.NotNull(active);
        Assert.Contains(active, c => c.Id == contract.Id);
        Assert.All(active, c => Assert.Equal("Active", c.Status));
    }

    [Fact]
    public async Task ListContracts_UnknownStatus_ReturnsValidationProblem()
    {
        var response = await _client.GetAsync("/api/contracts?status=paused");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
