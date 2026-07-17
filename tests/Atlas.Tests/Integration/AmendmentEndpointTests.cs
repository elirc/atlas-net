using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class AmendmentEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public AmendmentEndpointTests(AtlasApiFactory factory)
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

    private void EnsureCountry(string code)
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
                });
                db.SaveChanges();
            }
        });
    }

    private async Task<ContractResponse> CreateContractAsync(string prefix, string countryCode = "PH", bool activate = true)
    {
        EnsureCountry(countryCode);
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

    private async Task<AmendmentResponse> RequestAmendmentAsync(
        Guid contractId, decimal? newMonthlySalary, string? newJobTitle, string effectiveDate)
    {
        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId,
            newMonthlySalary,
            newJobTitle,
            effectiveDate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AmendmentResponse>())!;
    }

    [Fact]
    public async Task ContractCreation_SeedsInitialSalaryRecord()
    {
        var contract = await CreateContractAsync("initialrecord", activate: false);

        var history = await _client.GetFromJsonAsync<List<SalaryRecordResponse>>(
            $"/api/contracts/{contract.Id}/salary-history");

        var record = Assert.Single(history!);
        Assert.Equal("Initial", record.Source);
        Assert.Equal(100000m, record.MonthlySalary);
        Assert.Equal("Engineer", record.JobTitle);
        Assert.Equal(new DateOnly(2026, 1, 1), record.EffectiveDate);
    }

    [Fact]
    public async Task CreateAmendment_WithoutAnyChange_ReturnsValidationProblem()
    {
        var contract = await CreateContractAsync("nochange");

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = contract.Id,
            effectiveDate = "2026-09-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateAmendment_OnDraftContract_ReturnsConflict()
    {
        var contract = await CreateContractAsync("draftamend", activate: false);

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = contract.Id,
            newMonthlySalary = 120000,
            effectiveDate = "2026-09-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateAmendment_EffectiveBeforeContractStart_ReturnsConflict()
    {
        var contract = await CreateContractAsync("earlyamend");

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = contract.Id,
            newMonthlySalary = 120000,
            effectiveDate = "2025-12-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateAmendment_WhileAnotherIsPending_ReturnsConflict()
    {
        var contract = await CreateContractAsync("doublepending");
        await RequestAmendmentAsync(contract.Id, 120000m, null, "2026-09-01");

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = contract.Id,
            newMonthlySalary = 130000,
            effectiveDate = "2026-10-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ApproveAmendment_UpdatesContract_AndAppendsSalaryRecord()
    {
        var contract = await CreateContractAsync("approveamend");
        var amendment = await RequestAmendmentAsync(contract.Id, 150000m, "Staff Engineer", "2026-09-01");

        var response = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment.Id}/approve", new
        {
            note = "Annual review",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<AmendmentResponse>();
        Assert.Equal("Approved", approved!.Status);

        var updated = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contract.Id}");
        Assert.Equal(150000m, updated!.MonthlySalary);
        Assert.Equal("Staff Engineer", updated.JobTitle);

        var history = await _client.GetFromJsonAsync<List<SalaryRecordResponse>>(
            $"/api/contracts/{contract.Id}/salary-history");
        Assert.Equal(2, history!.Count);
        Assert.Equal("Initial", history[0].Source);
        Assert.Equal(100000m, history[0].MonthlySalary); // initial record is immutable
        Assert.Equal("Amendment", history[1].Source);
        Assert.Equal(150000m, history[1].MonthlySalary);
        Assert.Equal(amendment.Id, history[1].AmendmentId);
        Assert.Equal(new DateOnly(2026, 9, 1), history[1].EffectiveDate);
    }

    [Fact]
    public async Task RejectAmendment_LeavesContractAndHistoryUntouched()
    {
        var contract = await CreateContractAsync("rejectamend");
        var amendment = await RequestAmendmentAsync(contract.Id, 200000m, null, "2026-09-01");

        var response = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment.Id}/reject", new
        {
            note = "Out of budget",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contract.Id}");
        Assert.Equal(100000m, updated!.MonthlySalary);
        var history = await _client.GetFromJsonAsync<List<SalaryRecordResponse>>(
            $"/api/contracts/{contract.Id}/salary-history");
        Assert.Single(history!);

        // A new amendment can now be requested.
        var second = await RequestAmendmentAsync(contract.Id, 110000m, null, "2026-10-01");
        Assert.Equal("Pending", second.Status);
    }

    [Fact]
    public async Task CancelAmendment_Pending_BecomesCancelled()
    {
        var contract = await CreateContractAsync("cancelamend");
        var amendment = await RequestAmendmentAsync(contract.Id, 120000m, null, "2026-09-01");

        var response = await _client.PostAsync($"/api/contract-amendments/{amendment.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<AmendmentResponse>();
        Assert.Equal("Cancelled", cancelled!.Status);
    }

    [Fact]
    public async Task ApproveAmendment_Twice_ReturnsConflict()
    {
        var contract = await CreateContractAsync("twiceamend");
        var amendment = await RequestAmendmentAsync(contract.Id, 120000m, null, "2026-09-01");
        await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment.Id}/approve", new { });

        var second = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment.Id}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Payroll_UsesSalaryEffectiveForEachPeriod()
    {
        var contract = await CreateContractAsync("periodpay", countryCode: "QD");
        var amendment = await RequestAmendmentAsync(contract.Id, 150000m, null, "2026-08-01");
        await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment.Id}/approve", new { });

        // July: old salary despite the approved (future-dated) amendment.
        var julyResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QD", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, julyResponse.StatusCode);
        var july = await julyResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        Assert.Equal(100000m, july!.Payslips.Single(p => p.ContractId == contract.Id).GrossSalary);

        // August: amended salary applies.
        var augustResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QD", year = 2026, month = 8 });
        Assert.Equal(HttpStatusCode.Created, augustResponse.StatusCode);
        var august = await augustResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var payslip = august!.Payslips.Single(p => p.ContractId == contract.Id);
        Assert.Equal(150000m, payslip.GrossSalary);
        Assert.Equal(18000m, payslip.EmployerCost);      // 12% of 150000
        Assert.Equal(22500m, payslip.EmployeeDeductions); // 15% of 150000
    }

    [Fact]
    public async Task Amendments_AsOtherClientsViewer_AreHidden()
    {
        var contract = await CreateContractAsync("amendscope");
        var amendment = await RequestAmendmentAsync(contract.Id, 120000m, null, "2026-09-01");

        var otherClientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Amendment Outsider Co",
            billingEmail = "amendmentoutsider@example.com",
            headquartersCountryCode = "PH",
        });
        var otherClient = await otherClientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Amendment outsider viewer",
            role = "ClientViewer",
            clientId = otherClient!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var outsider = _factory.CreateClientWithApiKey(key);

        var list = await outsider.GetFromJsonAsync<List<AmendmentResponse>>("/api/contract-amendments");
        var direct = await outsider.GetAsync($"/api/contract-amendments/{amendment.Id}");
        var history = await outsider.GetAsync($"/api/contracts/{contract.Id}/salary-history");

        Assert.DoesNotContain(list!, a => a.Id == amendment.Id);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
    }
}
