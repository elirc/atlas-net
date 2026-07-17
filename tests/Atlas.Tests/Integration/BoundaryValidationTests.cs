using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

/// <summary>
/// Input boundaries and window edges: leave requests that touch existing
/// requests or the contract start, exact-fit balances, allowance bounds,
/// pagination limits, and empty-collection behavior.
/// </summary>
public class BoundaryValidationTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public BoundaryValidationTests(AtlasApiFactory factory)
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

    private async Task<Guid> CreateActiveContractAsync(string prefix, string startDate = "2026-01-01")
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
            countryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = 100_000,
            startDate,
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
        return contract.Id;
    }

    private async Task<HttpResponseMessage> RequestLeaveAsync(
        Guid contractId, string start, string end, string type = "Annual") =>
        await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId,
            type,
            startDate = start,
            endDate = end,
        });

    [Fact]
    public async Task LeaveRequest_TouchingExistingEndDate_IsAnOverlapConflict()
    {
        var contractId = await CreateActiveContractAsync("touchend");
        var first = await RequestLeaveAsync(contractId, "2026-08-03", "2026-08-05"); // Mon-Wed
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Starting exactly on the existing request's last day overlaps.
        var touching = await RequestLeaveAsync(contractId, "2026-08-05", "2026-08-07");

        Assert.Equal(HttpStatusCode.Conflict, touching.StatusCode);
        Assert.Contains("overlaps", await touching.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LeaveRequest_StartingTheDayAfterExistingEnd_Succeeds()
    {
        var contractId = await CreateActiveContractAsync("adjacent");
        var first = await RequestLeaveAsync(contractId, "2026-08-03", "2026-08-05"); // Mon-Wed
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Thursday, immediately after — adjacent but disjoint.
        var adjacent = await RequestLeaveAsync(contractId, "2026-08-06", "2026-08-07");

        Assert.Equal(HttpStatusCode.Created, adjacent.StatusCode);
    }

    [Fact]
    public async Task LeaveRequest_EndingExactlyOnExistingStart_IsAnOverlapConflict()
    {
        var contractId = await CreateActiveContractAsync("touchstart");
        var first = await RequestLeaveAsync(contractId, "2026-08-12", "2026-08-14"); // Wed-Fri
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var touching = await RequestLeaveAsync(contractId, "2026-08-10", "2026-08-12");

        Assert.Equal(HttpStatusCode.Conflict, touching.StatusCode);
        Assert.Contains("overlaps", await touching.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LeaveRequest_StartingExactlyOnContractStartDate_Succeeds()
    {
        // 2026-06-01 is a Monday.
        var contractId = await CreateActiveContractAsync("onstart", startDate: "2026-06-01");

        var response = await RequestLeaveAsync(contractId, "2026-06-01", "2026-06-02");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task LeaveRequest_StartingOneDayBeforeContractStart_IsConflict()
    {
        var contractId = await CreateActiveContractAsync("beforestart", startDate: "2026-06-02");

        var response = await RequestLeaveAsync(contractId, "2026-06-01", "2026-06-03");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot start before the contract start date", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LeaveRequest_ConsumingExactRemainingBalance_Succeeds_ThenOneMoreDayConflicts()
    {
        var contractId = await CreateActiveContractAsync("exactfit");

        // Policy: 15 annual days. Take 10 (two full weeks), then exactly 5 more.
        var first = await RequestLeaveAsync(contractId, "2026-03-02", "2026-03-13");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(10, (await first.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Days);

        var exact = await RequestLeaveAsync(contractId, "2026-05-04", "2026-05-08");
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        Assert.Equal(5, (await exact.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Days);

        // Balance is now zero; a single further working day must fail.
        var overdraft = await RequestLeaveAsync(contractId, "2026-07-06", "2026-07-06");
        Assert.Equal(HttpStatusCode.Conflict, overdraft.StatusCode);
        Assert.Contains("Insufficient annual leave balance", await overdraft.Content.ReadAsStringAsync());

        var balances = await _client.GetFromJsonAsync<ContractLeaveBalancesResponse>(
            $"/api/contracts/{contractId}/leave-balances?year=2026");
        var annual = balances!.Balances.Single(b => b.Type == "Annual");
        Assert.Equal(0, annual.RemainingDays);
    }

    [Fact]
    public async Task LeaveBalance_IsPerCalendarYear_NextYearStartsFresh()
    {
        var contractId = await CreateActiveContractAsync("yearreset");
        var thisYear = await RequestLeaveAsync(contractId, "2026-03-02", "2026-03-13"); // 10 days
        Assert.Equal(HttpStatusCode.Created, thisYear.StatusCode);

        // Same worker, next calendar year: full allowance again.
        var nextYear = await RequestLeaveAsync(contractId, "2027-03-01", "2027-03-12"); // 10 days
        Assert.Equal(HttpStatusCode.Created, nextYear.StatusCode);

        var balances2027 = await _client.GetFromJsonAsync<ContractLeaveBalancesResponse>(
            $"/api/contracts/{contractId}/leave-balances?year=2027");
        var annual = balances2027!.Balances.Single(b => b.Type == "Annual");
        Assert.Equal(5, annual.RemainingDays);
    }

    [Theory]
    [InlineData(366, HttpStatusCode.Created)]
    [InlineData(367, HttpStatusCode.BadRequest)]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    [InlineData(0, HttpStatusCode.Created)]
    public async Task LeavePolicy_AllowanceBounds_AreEnforcedInclusively(int days, HttpStatusCode expected)
    {
        // A fresh country per case: one policy per country.
        var code = $"L{Math.Abs(days) % 10}";
        var country = await _client.PostAsJsonAsync("/api/countries", new
        {
            code,
            name = $"Leaveland {code}",
            currencyCode = "PHP",
            employerCostRate = 0.1,
            employeeDeductionRate = 0.1,
        });
        Assert.Equal(HttpStatusCode.Created, country.StatusCode);

        var response = await _client.PostAsJsonAsync("/api/leave-policies", new
        {
            countryCode = code,
            annualLeaveDays = days,
            sickLeaveDays = 5,
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Pagination_MaxPageSize_IsAcceptedInclusively()
    {
        var response = await _client.GetAsync("/api/workers?page=1&pageSize=200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("200", response.Headers.GetValues("X-Page-Size").Single());
    }

    [Fact]
    public async Task Pagination_PageBeyondData_ReturnsEmptyPageWithTrueTotal()
    {
        await CreateActiveContractAsync("pagedout");

        var response = await _client.GetAsync("/api/contracts?page=999&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<ContractResponse>>();
        Assert.Empty(items!);
        Assert.True(int.Parse(response.Headers.GetValues("X-Total-Count").Single()) >= 1);
        Assert.Equal("999", response.Headers.GetValues("X-Page").Single());
    }

    [Fact]
    public async Task EmptyListEndpoints_ReturnEmptyArraysWithZeroTotal()
    {
        // A dedicated factory: nothing has been created in it beyond the seed key.
        using var fresh = new AtlasApiFactory();
        var client = fresh.CreateClient();
        string[] paths =
        [
            "/api/clients",
            "/api/workers",
            "/api/contracts",
            "/api/leave-requests",
            "/api/expense-claims",
            "/api/contract-amendments",
            "/api/termination-requests",
            "/api/benefit-enrollments",
            "/api/invoices",
            "/api/payroll-runs",
            "/api/fx-rates",
        ];

        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);

            Assert.True(HttpStatusCode.OK == response.StatusCode, $"GET {path} returned {(int)response.StatusCode}.");
            Assert.Equal("0", response.Headers.GetValues("X-Total-Count").Single());
            var body = (await response.Content.ReadAsStringAsync()).Trim();
            Assert.Equal("[]", body);
        }
    }

    [Theory]
    [InlineData("/api/contracts?status=Bogus")]
    [InlineData("/api/leave-requests?status=Bogus")]
    [InlineData("/api/expense-claims?status=Bogus")]
    [InlineData("/api/contract-amendments?status=Bogus")]
    [InlineData("/api/termination-requests?status=Bogus")]
    [InlineData("/api/benefit-enrollments?status=Bogus")]
    public async Task ListEndpoints_UnknownStatusFilter_ReturnValidationProblem(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Unknown status 'Bogus'", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Amendment_EffectiveExactlyOnContractStartDate_IsAccepted()
    {
        var contractId = await CreateActiveContractAsync("amendonstart", startDate: "2026-02-01");

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId,
            newMonthlySalary = 120_000,
            effectiveDate = "2026-02-01",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task Amendment_NonPositiveSalary_ReturnsValidationProblem(decimal salary)
    {
        var contractId = await CreateActiveContractAsync($"amendbad{Math.Abs(salary)}");

        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId,
            newMonthlySalary = salary,
            effectiveDate = "2026-06-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NewMonthlySalary must be greater than zero", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TerminationRequest_EndDateExactlyAtMinimumNotice_IsAccepted()
    {
        var contractId = await CreateActiveContractAsync("noticeedge");

        // PH default notice: 30 days from today (the notice date).
        var earliest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var exact = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId,
            reason = "Boundary check",
            proposedEndDate = earliest.ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
    }

    [Fact]
    public async Task TerminationRequest_EndDateOneDayShortOfNotice_IsConflict()
    {
        var contractId = await CreateActiveContractAsync("noticeshort");

        var tooEarly = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(29);
        var response = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId,
            reason = "Boundary check",
            proposedEndDate = tooEarly.ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("requires 30 day(s) of notice", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpenseClaim_ManyItems_TotalIsExactSumOfCents()
    {
        var contractId = await CreateActiveContractAsync("centsum");
        var items = Enumerable.Range(1, 25)
            .Select(i => new { description = $"Line {i}", amount = 0.01m + i * 0.10m, incurredDate = "2026-05-01" })
            .ToArray();

        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId,
            items,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ExpenseClaimResponse>();
        var expected = items.Sum(i => i.amount);
        Assert.Equal(expected, claim!.TotalAmount);
        Assert.Equal(25, claim.Items.Count);
    }
}
