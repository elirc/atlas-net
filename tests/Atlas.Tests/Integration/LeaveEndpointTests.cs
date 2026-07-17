using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class LeaveEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public LeaveEndpointTests(AtlasApiFactory factory)
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
            // A country with no leave policy, for the missing-policy test.
            if (!db.Countries.Any(c => c.Code == "VN"))
            {
                db.Countries.Add(new Country
                {
                    Code = "VN",
                    Name = "Vietnam",
                    CurrencyCode = "VND",
                    EmployerCostRate = 0.17m,
                    EmployeeDeductionRate = 0.10m,
                });
            }
            db.SaveChanges();
        });
    }

    /// <summary>Creates a client + worker + active contract starting 2026-01-01 in the given country.</summary>
    private async Task<ContractResponse> CreateActiveContractAsync(string prefix, string countryCode = "PH")
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, clientResponse.StatusCode);
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();

        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{prefix} Worker",
            email = $"{prefix}.worker@example.com",
            countryCode,
        });
        Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, contractResponse.StatusCode);
        var contract = await contractResponse.Content.ReadFromJsonAsync<ContractResponse>();

        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired && !i.IsCompleted))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });
        var activate = await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        return contract;
    }

    private async Task<LeaveRequestResponse> RequestLeaveAsync(
        Guid contractId, string type, string start, string end)
    {
        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId,
            type,
            startDate = start,
            endDate = end,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LeaveRequestResponse>())!;
    }

    [Fact]
    public async Task CreateLeavePolicy_Duplicate_ReturnsConflict()
    {
        var response = await _client.PostAsJsonAsync("/api/leave-policies", new
        {
            countryCode = "PH",
            annualLeaveDays = 20,
            sickLeaveDays = 10,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_ComputesWorkingDays_AndStartsPending()
    {
        var contract = await CreateActiveContractAsync("computedays");

        // Mon 2026-08-03 .. Sun 2026-08-09: 5 working days.
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-09");

        Assert.Equal("Pending", request.Status);
        Assert.Equal(5, request.Days);
        Assert.Equal("Annual", request.Type);
    }

    [Fact]
    public async Task CreateLeaveRequest_OnDraftContract_ReturnsConflict()
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Draft Leave Co",
            billingEmail = "draftleave@example.com",
            headquartersCountryCode = "PH",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Draft Leave Worker",
            email = "draftleave.worker@example.com",
            countryCode = "PH",
        });
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();
        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate = "2026-01-01",
        });
        var contract = await contractResponse.Content.ReadFromJsonAsync<ContractResponse>();

        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract!.Id,
            type = "Annual",
            startDate = "2026-08-03",
            endDate = "2026-08-04",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_OverlappingPending_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("overlap");
        await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Sick",
            startDate = "2026-08-06",
            endDate = "2026-08-10",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_AfterRejection_SameDatesSucceed()
    {
        var contract = await CreateActiveContractAsync("rerequest");
        var first = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        var reject = await _client.PostAsJsonAsync($"/api/leave-requests/{first.Id}/reject", new
        {
            note = "Release week",
        });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var second = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");
        Assert.Equal("Pending", second.Status);
    }

    [Fact]
    public async Task CreateLeaveRequest_ExceedingBalance_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("exceed");

        // 15 annual days allowed; 2026-03-02 .. 2026-03-20 is 15 working days.
        await RequestLeaveAsync(contract.Id, "Annual", "2026-03-02", "2026-03-20");

        // One more annual day should not fit.
        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-04-01",
            endDate = "2026-04-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_SickBalance_IsSeparateFromAnnual()
    {
        var contract = await CreateActiveContractAsync("sicksplit");
        await RequestLeaveAsync(contract.Id, "Annual", "2026-03-02", "2026-03-20"); // exhausts annual

        var sick = await RequestLeaveAsync(contract.Id, "Sick", "2026-04-06", "2026-04-07");

        Assert.Equal("Pending", sick.Status);
        Assert.Equal(2, sick.Days);
    }

    [Fact]
    public async Task CreateLeaveRequest_SpanningYears_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("yearspan");

        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-12-28",
            endDate = "2027-01-05",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_WeekendOnly_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("weekend");

        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-08-01",
            endDate = "2026-08-02",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeaveRequest_CountryWithoutPolicy_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("nopolicy", countryCode: "VN");

        var response = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-08-03",
            endDate = "2026-08-04",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ApproveLeaveRequest_Pending_BecomesApproved()
    {
        var contract = await CreateActiveContractAsync("approve");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        var response = await _client.PostAsJsonAsync($"/api/leave-requests/{request.Id}/approve", new
        {
            note = "Have fun",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<LeaveRequestResponse>();
        Assert.Equal("Approved", approved!.Status);
        Assert.Equal("Have fun", approved.DecisionNote);
        Assert.NotNull(approved.DecidedAtUtc);
    }

    [Fact]
    public async Task ApproveLeaveRequest_Twice_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("doubleapprove");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");
        await _client.PostAsJsonAsync($"/api/leave-requests/{request.Id}/approve", new { });

        var second = await _client.PostAsJsonAsync($"/api/leave-requests/{request.Id}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task RejectLeaveRequest_WithoutNote_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("rejectnonote");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        var response = await _client.PostAsJsonAsync($"/api/leave-requests/{request.Id}/reject", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CancelLeaveRequest_Pending_BecomesCancelled()
    {
        var contract = await CreateActiveContractAsync("cancel");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        var response = await _client.PostAsync($"/api/leave-requests/{request.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<LeaveRequestResponse>();
        Assert.Equal("Cancelled", cancelled!.Status);
    }

    [Fact]
    public async Task LeaveBalances_ReflectApprovedAndPendingDays()
    {
        var contract = await CreateActiveContractAsync("balances");
        var approved = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07"); // 5 days
        await _client.PostAsJsonAsync($"/api/leave-requests/{approved.Id}/approve", new { });
        await RequestLeaveAsync(contract.Id, "Annual", "2026-09-07", "2026-09-08"); // 2 days pending
        await RequestLeaveAsync(contract.Id, "Sick", "2026-10-05", "2026-10-05");   // 1 day pending

        var response = await _client.GetFromJsonAsync<ContractLeaveBalancesResponse>(
            $"/api/contracts/{contract.Id}/leave-balances?year=2026");

        Assert.Equal(2026, response!.Year);
        var annual = response.Balances.Single(b => b.Type == "Annual");
        Assert.Equal(15, annual.AllowanceDays);
        Assert.Equal(5, annual.ApprovedDays);
        Assert.Equal(2, annual.PendingDays);
        Assert.Equal(8, annual.RemainingDays);
        var sick = response.Balances.Single(b => b.Type == "Sick");
        Assert.Equal(10, sick.AllowanceDays);
        Assert.Equal(0, sick.ApprovedDays);
        Assert.Equal(1, sick.PendingDays);
        Assert.Equal(9, sick.RemainingDays);
    }

    [Fact]
    public async Task LeaveRequests_ListFilterByStatus_ReturnsMatchesOnly()
    {
        var contract = await CreateActiveContractAsync("filterleave");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");
        await _client.PostAsJsonAsync($"/api/leave-requests/{request.Id}/approve", new { });

        var approved = await _client.GetFromJsonAsync<List<LeaveRequestResponse>>(
            $"/api/leave-requests?contractId={contract.Id}&status=approved");

        var only = Assert.Single(approved!);
        Assert.Equal(request.Id, only.Id);
        Assert.Equal("Approved", only.Status);
    }

    [Fact]
    public async Task LeaveRequests_AsOtherClientsViewer_AreHidden()
    {
        var contract = await CreateActiveContractAsync("leavescope");
        var request = await RequestLeaveAsync(contract.Id, "Annual", "2026-08-03", "2026-08-07");

        // A viewer for a different client cannot see or decide the request.
        var otherClientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Leave Outsider Co",
            billingEmail = "leaveoutsider@example.com",
            headquartersCountryCode = "PH",
        });
        var otherClient = await otherClientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Leave outsider viewer",
            role = "ClientViewer",
            clientId = otherClient!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var outsider = _factory.CreateClientWithApiKey(key);

        var list = await outsider.GetFromJsonAsync<List<LeaveRequestResponse>>("/api/leave-requests");
        var direct = await outsider.GetAsync($"/api/leave-requests/{request.Id}");
        var decide = await outsider.PostAsJsonAsync($"/api/leave-requests/{request.Id}/approve", new { });

        Assert.DoesNotContain(list!, r => r.Id == request.Id);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, decide.StatusCode);
    }
}
