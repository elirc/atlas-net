using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class TerminationEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public TerminationEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        EnsureCountry("PH");
    }

    private void EnsureCountry(string code, int? annualLeaveDays = 15)
    {
        _factory.WithDb(db =>
        {
            if (!db.Countries.Any(c => c.Code == code))
            {
                db.Countries.Add(new Country
                {
                    Code = code,
                    Name = $"Testland {code}",
                    CurrencyCode = "PHP",
                    EmployerCostRate = 0.12m,
                    EmployeeDeductionRate = 0.15m,
                    MinimumNoticeDays = 30,
                });
            }
            if (annualLeaveDays is not null && !db.LeavePolicies.Any(p => p.CountryCode == code))
            {
                db.LeavePolicies.Add(new LeavePolicy
                {
                    CountryCode = code,
                    AnnualLeaveDays = annualLeaveDays.Value,
                    SickLeaveDays = 10,
                });
            }
            db.SaveChanges();
        });
    }

    private async Task<ContractResponse> CreateContractAsync(string prefix, string countryCode = "PH", bool activate = true)
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = countryCode,
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

        if (activate)
        {
            _factory.WithDb(db =>
            {
                foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired && !i.IsCompleted))
                {
                    item.Complete(DateTimeOffset.UtcNow);
                }
                db.SaveChanges();
            });
            var activateResponse = await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        }

        return contract!;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task CreateTerminationRequest_WithSufficientNotice_StartsPending()
    {
        var contract = await CreateContractAsync("noticeok");

        var response = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Role eliminated",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = await response.Content.ReadFromJsonAsync<TerminationRequestResponse>();
        Assert.Equal("Pending", request!.Status);
        Assert.Equal(Today, request.NoticeDate);
    }

    [Fact]
    public async Task CreateTerminationRequest_InsufficientNotice_ReturnsConflict()
    {
        var contract = await CreateContractAsync("noticeshort");

        var response = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Too fast",
            proposedEndDate = Today.AddDays(10).ToString("yyyy-MM-dd"), // PH requires 30 days
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTerminationRequest_OnDraftContract_ReturnsConflict()
    {
        var contract = await CreateContractAsync("noticedraft", activate: false);

        var response = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Never started",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTerminationRequest_WhileAnotherIsPending_ReturnsConflict()
    {
        var contract = await CreateContractAsync("noticedouble");
        var first = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "First",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Second",
            proposedEndDate = Today.AddDays(60).ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ApproveTerminationRequest_TerminatesContract()
    {
        var contract = await CreateContractAsync("noticeapprove");
        var endDate = Today.AddDays(45);
        var createResponse = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Project wind-down",
            proposedEndDate = endDate.ToString("yyyy-MM-dd"),
        });
        var request = await createResponse.Content.ReadFromJsonAsync<TerminationRequestResponse>();

        var approveResponse = await _client.PostAsJsonAsync($"/api/termination-requests/{request!.Id}/approve", new
        {
            note = "Confirmed",
        });

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approved = await approveResponse.Content.ReadFromJsonAsync<TerminationRequestResponse>();
        Assert.Equal("Approved", approved!.Status);

        var terminated = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contract.Id}");
        Assert.Equal("Terminated", terminated!.Status);
        Assert.Equal(endDate, terminated.EndDate);
        Assert.Equal("Project wind-down", terminated.TerminationReason);
    }

    [Fact]
    public async Task RejectTerminationRequest_WithoutNote_ReturnsConflict()
    {
        var contract = await CreateContractAsync("noticereject");
        var createResponse = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Maybe",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });
        var request = await createResponse.Content.ReadFromJsonAsync<TerminationRequestResponse>();

        var response = await _client.PostAsJsonAsync($"/api/termination-requests/{request!.Id}/reject", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CancelTerminationRequest_KeepsContractActive()
    {
        var contract = await CreateContractAsync("noticecancel");
        var createResponse = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Reorg",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });
        var request = await createResponse.Content.ReadFromJsonAsync<TerminationRequestResponse>();

        var cancelResponse = await _client.PostAsync($"/api/termination-requests/{request!.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var stillActive = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contract.Id}");
        Assert.Equal("Active", stillActive!.Status);
    }

    [Fact]
    public async Task FinalPayroll_ProratesSalary_AndPaysOutUnusedLeave()
    {
        EnsureCountry("QK"); // annual allowance 15
        var contract = await CreateContractAsync("finalpay", countryCode: "QK");

        // 5 approved annual leave days in the end year -> 10 unused.
        var leaveResponse = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-03-02",
            endDate = "2026-03-06",
        });
        Assert.Equal(HttpStatusCode.Created, leaveResponse.StatusCode);
        var leave = await leaveResponse.Content.ReadFromJsonAsync<LeaveRequestResponse>();
        await _client.PostAsJsonAsync($"/api/leave-requests/{leave!.Id}/approve", new { });

        // Terminate for cause (immediate path), effective mid-July.
        var terminate = await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-07-15",
            reason = "Gross misconduct",
        });
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QK", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var payslip = run!.Payslips.Single(p => p.ContractId == contract.Id);

        // Prorated: 100000 * 15/31 = 48387.10; payout: 10 * 4615.38 = 46153.80.
        Assert.Equal(46153.80m, payslip.UnusedLeavePayout);
        Assert.Equal(94540.90m, payslip.GrossSalary); // 48387.10 + 46153.80
        Assert.Equal(11344.91m, payslip.EmployerCost); // 12%
        Assert.Equal(14181.14m, payslip.EmployeeDeductions); // 15%
        Assert.Equal(80359.76m, payslip.NetPay);
        Assert.Equal(105885.81m, payslip.TotalCost);

        // The month after the end date has no payable contracts.
        var augustResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QK", year = 2026, month = 8 });
        Assert.Equal(HttpStatusCode.Conflict, augustResponse.StatusCode);
    }

    [Fact]
    public async Task FinalPayroll_WithoutLeavePolicy_PaysProratedSalaryOnly()
    {
        EnsureCountry("QL", annualLeaveDays: null); // no leave policy
        var contract = await CreateContractAsync("finalnopolicy", countryCode: "QL");

        await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-07-15",
            reason = "End of project",
        });

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QL", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var payslip = run!.Payslips.Single(p => p.ContractId == contract.Id);

        Assert.Equal(0m, payslip.UnusedLeavePayout);
        Assert.Equal(48387.10m, payslip.GrossSalary);
    }

    [Fact]
    public async Task EarlierMonths_AreNotProrated()
    {
        EnsureCountry("QM");
        var contract = await CreateContractAsync("finalearlier", countryCode: "QM");

        await _client.PostAsJsonAsync($"/api/contracts/{contract.Id}/terminate", new
        {
            endDate = "2026-07-15",
            reason = "End of project",
        });

        // June is a full month even though the contract is already terminated.
        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QM", year = 2026, month = 6 });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var payslip = run!.Payslips.Single(p => p.ContractId == contract.Id);

        Assert.Equal(0m, payslip.UnusedLeavePayout);
        Assert.Equal(100000m, payslip.GrossSalary);
    }

    [Fact]
    public async Task TerminationRequests_AsOtherClientsViewer_AreHidden()
    {
        var contract = await CreateContractAsync("noticescope");
        var createResponse = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Secret reorg",
            proposedEndDate = Today.AddDays(45).ToString("yyyy-MM-dd"),
        });
        var request = await createResponse.Content.ReadFromJsonAsync<TerminationRequestResponse>();

        var otherClientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Termination Outsider Co",
            billingEmail = "terminationoutsider@example.com",
            headquartersCountryCode = "PH",
        });
        var otherClient = await otherClientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Termination outsider viewer",
            role = "ClientViewer",
            clientId = otherClient!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var outsider = _factory.CreateClientWithApiKey(key);

        var list = await outsider.GetFromJsonAsync<List<TerminationRequestResponse>>("/api/termination-requests");
        var direct = await outsider.GetAsync($"/api/termination-requests/{request!.Id}");
        var approve = await outsider.PostAsJsonAsync($"/api/termination-requests/{request.Id}/approve", new { });

        Assert.DoesNotContain(list!, t => t.Id == request.Id);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
    }
}
