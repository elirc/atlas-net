using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class AuthorizationTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _admin;

    public AuthorizationTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClient();
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

    private async Task<Guid> CreateClientCompanyAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/clients", new
        {
            name,
            billingEmail = $"{name.ToLowerInvariant().Replace(' ', '.')}@example.com",
            headquartersCountryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var client = await response.Content.ReadFromJsonAsync<ClientResponse>();
        return client!.Id;
    }

    private async Task<Guid> CreateWorkerAsync(string emailPrefix)
    {
        var response = await _admin.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{emailPrefix} Worker",
            email = $"{emailPrefix}.worker@example.com",
            countryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var worker = await response.Content.ReadFromJsonAsync<WorkerResponse>();
        return worker!.Id;
    }

    private async Task<ContractResponse> CreateContractAsync(Guid clientId, Guid workerId)
    {
        var response = await _admin.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContractResponse>())!;
    }

    /// <summary>Issues an API key for the role/client through the api-users endpoint.</summary>
    private async Task<string> IssueKeyAsync(string role, Guid? clientId, string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/api-users", new { name, role, clientId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ApiUserCreatedResponse>();
        return created!.ApiKey;
    }

    [Fact]
    public async Task Request_WithoutApiKey_Returns401()
    {
        var anonymous = _factory.CreateClientWithApiKey(null);

        var response = await anonymous.GetAsync("/api/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithUnknownApiKey_Returns401()
    {
        var stranger = _factory.CreateClientWithApiKey("not-a-real-key");

        var response = await stranger.GetAsync("/api/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutApiKey_IsOpen()
    {
        var anonymous = _factory.CreateClientWithApiKey(null);

        var response = await anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeactivatedApiKey_Returns401()
    {
        var clientId = await CreateClientCompanyAsync("Deactivate Co");
        var key = await IssueKeyAsync("ClientViewer", clientId, "Soon revoked");
        var users = await _admin.GetFromJsonAsync<List<ApiUserResponse>>("/api/api-users");
        var userId = users!.Single(u => u.Name == "Soon revoked").Id;

        var deactivate = await _admin.PostAsync($"/api/api-users/{userId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var revoked = _factory.CreateClientWithApiKey(key);
        var response = await revoked.GetAsync("/api/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateCountry_AsClientAdmin_Returns403()
    {
        var clientId = await CreateClientCompanyAsync("NoCountries Co");
        var key = await IssueKeyAsync("ClientAdmin", clientId, "NoCountries admin");
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.PostAsJsonAsync("/api/countries", new
        {
            code = "XX",
            name = "Nowhere",
            currencyCode = "XXX",
            employerCostRate = 0.1,
            employeeDeductionRate = 0.1,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PayrollRuns_AsClientAdmin_Returns403()
    {
        var clientId = await CreateClientCompanyAsync("NoPayroll Co");
        var key = await IssueKeyAsync("ClientAdmin", clientId, "NoPayroll admin");
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.GetAsync("/api/payroll-runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListClients_AsClientUser_ReturnsOwnClientOnly()
    {
        var ownId = await CreateClientCompanyAsync("Own Co");
        await CreateClientCompanyAsync("Other One Co");
        var key = await IssueKeyAsync("ClientViewer", ownId, "Own viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var clients = await viewer.GetFromJsonAsync<List<ClientResponse>>("/api/clients");

        var only = Assert.Single(clients!);
        Assert.Equal(ownId, only.Id);
    }

    [Fact]
    public async Task GetOtherClient_AsClientUser_Returns404()
    {
        var ownId = await CreateClientCompanyAsync("Peeker Co");
        var otherId = await CreateClientCompanyAsync("Hidden Co");
        var key = await IssueKeyAsync("ClientViewer", ownId, "Peeker viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var response = await viewer.GetAsync($"/api/clients/{otherId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListContracts_AsClientUser_IsScopedEvenWithForeignFilter()
    {
        var ownId = await CreateClientCompanyAsync("Scoped Co");
        var otherId = await CreateClientCompanyAsync("Foreign Co");
        var ownContract = await CreateContractAsync(ownId, await CreateWorkerAsync("scoped"));
        var otherContract = await CreateContractAsync(otherId, await CreateWorkerAsync("foreign"));
        var key = await IssueKeyAsync("ClientViewer", ownId, "Scoped viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var unfiltered = await viewer.GetFromJsonAsync<List<ContractResponse>>("/api/contracts");
        var filtered = await viewer.GetFromJsonAsync<List<ContractResponse>>($"/api/contracts?clientId={otherId}");

        Assert.Contains(unfiltered!, c => c.Id == ownContract.Id);
        Assert.DoesNotContain(unfiltered!, c => c.Id == otherContract.Id);
        Assert.Empty(filtered!);
    }

    [Fact]
    public async Task GetOtherClientsContract_Returns404()
    {
        var ownId = await CreateClientCompanyAsync("Reader Co");
        var otherId = await CreateClientCompanyAsync("Sealed Co");
        var otherContract = await CreateContractAsync(otherId, await CreateWorkerAsync("sealed"));
        var key = await IssueKeyAsync("ClientViewer", ownId, "Reader viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var response = await viewer.GetAsync($"/api/contracts/{otherContract.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_AsClientViewer_Returns403()
    {
        var clientId = await CreateClientCompanyAsync("ViewOnly Co");
        var workerId = await CreateWorkerAsync("viewonly");
        var key = await IssueKeyAsync("ClientViewer", clientId, "ViewOnly viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var response = await viewer.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_AsClientAdmin_ForOtherClient_Returns403()
    {
        var ownId = await CreateClientCompanyAsync("Bounded Co");
        var otherId = await CreateClientCompanyAsync("Target Co");
        var workerId = await CreateWorkerAsync("bounded");
        var key = await IssueKeyAsync("ClientAdmin", ownId, "Bounded admin");
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.PostAsJsonAsync("/api/contracts", new
        {
            clientId = otherId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateContract_AsClientAdmin_ForOwnClient_Succeeds()
    {
        var ownId = await CreateClientCompanyAsync("Allowed Co");
        var workerId = await CreateWorkerAsync("allowed");
        var key = await IssueKeyAsync("ClientAdmin", ownId, "Allowed admin");
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.PostAsJsonAsync("/api/contracts", new
        {
            clientId = ownId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CompleteOnboarding_AsClientViewer_Returns403()
    {
        var clientId = await CreateClientCompanyAsync("Checklist Co");
        var contract = await CreateContractAsync(clientId, await CreateWorkerAsync("checklist"));
        var key = await IssueKeyAsync("ClientViewer", clientId, "Checklist viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var checklist = await viewer.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        var item = checklist!.Items.First(i => !i.IsCompleted);

        var response = await viewer.PostAsJsonAsync(
            $"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListWorkers_AsClientUser_ReturnsOnlyContractedWorkers()
    {
        var ownId = await CreateClientCompanyAsync("Roster Co");
        var otherId = await CreateClientCompanyAsync("Elsewhere Co");
        var ownWorkerId = await CreateWorkerAsync("roster");
        var otherWorkerId = await CreateWorkerAsync("elsewhere");
        await CreateContractAsync(ownId, ownWorkerId);
        await CreateContractAsync(otherId, otherWorkerId);
        var key = await IssueKeyAsync("ClientViewer", ownId, "Roster viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var workers = await viewer.GetFromJsonAsync<List<WorkerResponse>>("/api/workers");
        var otherDirect = await viewer.GetAsync($"/api/workers/{otherWorkerId}");

        Assert.Contains(workers!, w => w.Id == ownWorkerId);
        Assert.DoesNotContain(workers!, w => w.Id == otherWorkerId);
        Assert.Equal(HttpStatusCode.NotFound, otherDirect.StatusCode);
    }

    [Fact]
    public async Task Invoices_AsClientUser_AreScopedToOwnClient()
    {
        var ownId = await CreateClientCompanyAsync("Billed Co");
        var otherId = await CreateClientCompanyAsync("AlsoBilled Co");
        var ownContract = await CreateContractAsync(ownId, await CreateWorkerAsync("billed"));
        var otherContract = await CreateContractAsync(otherId, await CreateWorkerAsync("alsobilled"));
        foreach (var contract in new[] { ownContract, otherContract })
        {
            _factory.WithDb(db =>
            {
                foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract.Id && i.IsRequired && !i.IsCompleted))
                {
                    item.Complete(DateTimeOffset.UtcNow);
                }
                db.SaveChanges();
            });
            var activate = await _admin.PostAsync($"/api/contracts/{contract.Id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        }

        var createRun = await _admin.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "PH", year = 2030, month = 1 });
        Assert.Equal(HttpStatusCode.Created, createRun.StatusCode);
        var run = await createRun.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var complete = await _admin.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var key = await IssueKeyAsync("ClientViewer", ownId, "Billed viewer");
        var viewer = _factory.CreateClientWithApiKey(key);

        var invoices = await viewer.GetFromJsonAsync<List<InvoiceResponse>>("/api/invoices");
        Assert.NotEmpty(invoices!);
        Assert.All(invoices!, i => Assert.Equal(ownId, i.ClientId));

        var adminInvoices = await _admin.GetFromJsonAsync<List<InvoiceResponse>>("/api/invoices");
        var otherInvoice = adminInvoices!.First(i => i.ClientId == otherId);
        var crossRead = await viewer.GetAsync($"/api/invoices/{otherInvoice.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
    }

    [Fact]
    public async Task CreateApiUser_ClientRoleWithoutClientId_ReturnsValidationProblem()
    {
        var response = await _admin.PostAsJsonAsync("/api/api-users", new
        {
            name = "Orphan viewer",
            role = "ClientViewer",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateApiUser_PlatformAdminWithClientId_ReturnsValidationProblem()
    {
        var clientId = await CreateClientCompanyAsync("Overreach Co");

        var response = await _admin.PostAsJsonAsync("/api/api-users", new
        {
            name = "Scoped platform admin",
            role = "PlatformAdmin",
            clientId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListApiUsers_MasksKeys()
    {
        var users = await _admin.GetFromJsonAsync<List<ApiUserResponse>>("/api/api-users");

        Assert.NotEmpty(users!);
        Assert.All(users!, u => Assert.StartsWith("****", u.ApiKeyMasked));
        Assert.All(users!, u => Assert.DoesNotContain(AtlasApiFactory.AdminApiKey, u.ApiKeyMasked));
    }

    [Fact]
    public async Task ApiUserEndpoints_AsClientAdmin_Returns403()
    {
        var clientId = await CreateClientCompanyAsync("KeyFactory Co");
        var key = await IssueKeyAsync("ClientAdmin", clientId, "KeyFactory admin");
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.PostAsJsonAsync("/api/api-users", new
        {
            name = "Sneaky admin",
            role = "PlatformAdmin",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
