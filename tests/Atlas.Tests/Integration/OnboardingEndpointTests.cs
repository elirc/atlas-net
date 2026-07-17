using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class OnboardingEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public OnboardingEndpointTests(AtlasApiFactory factory)
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

    private async Task<ContractResponse> CreateDraftContractAsync(string prefix)
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = "PH",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();

        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{prefix} Worker",
            email = $"{prefix}.worker@example.com",
            countryCode = "PH",
        });
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-08-01",
        });
        return (await contractResponse.Content.ReadFromJsonAsync<ContractResponse>())!;
    }

    [Fact]
    public async Task CreateContract_AutomaticallyCreatesDefaultChecklist()
    {
        var contract = await CreateDraftContractAsync("checklist");

        var checklist = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");

        Assert.NotNull(checklist);
        Assert.Equal(5, checklist.Items.Count);
        Assert.False(checklist.IsComplete);
        Assert.Contains(checklist.Items, i => i.Type == "RightToWorkCheck" && i.IsRequired);
        Assert.Contains(checklist.Items, i => i.Type == "TaxForms" && !i.IsRequired);
    }

    [Fact]
    public async Task CompleteItem_MarksItCompletedWithNotes()
    {
        var contract = await CreateDraftContractAsync("completeitem");
        var checklist = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        var item = checklist!.Items.First(i => i.Type == "BankDetails");

        var response = await _client.PostAsJsonAsync(
            $"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete",
            new { notes = "IBAN verified" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await response.Content.ReadFromJsonAsync<OnboardingItemResponse>();
        Assert.True(completed!.IsCompleted);
        Assert.Equal("IBAN verified", completed.Notes);
        Assert.NotNull(completed.CompletedAtUtc);
    }

    [Fact]
    public async Task CompleteItem_Twice_ReturnsConflict()
    {
        var contract = await CreateDraftContractAsync("completetwice");
        var checklist = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        var item = checklist!.Items[0];

        await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete", new { });
        var second = await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Activate_WithPendingRequiredItems_ReturnsConflictListingThem()
    {
        var contract = await CreateDraftContractAsync("blockedactivate");

        var response = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("required onboarding item", body);
    }

    [Fact]
    public async Task Activate_AfterCompletingAllRequiredItems_Succeeds()
    {
        var contract = await CreateDraftContractAsync("unblockedactivate");
        var checklist = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");

        foreach (var item in checklist!.Items.Where(i => i.IsRequired))
        {
            var complete = await _client.PostAsJsonAsync(
                $"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete", new { });
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var response = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        Assert.True(refreshed!.IsComplete); // optional items don't block completeness
    }

    [Fact]
    public async Task GetChecklist_UnknownContract_Returns404()
    {
        var response = await _client.GetAsync($"/api/contracts/{Guid.NewGuid()}/onboarding");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
