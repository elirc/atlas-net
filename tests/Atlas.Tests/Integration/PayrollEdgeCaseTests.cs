using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;

namespace Atlas.Tests.Integration;

/// <summary>
/// Payroll boundary behavior: month-edge contract starts, amendments effective
/// exactly on period edges, run-vs-amendment ordering, FX effective-date edges,
/// and leap-year final-pay proration.
/// </summary>
public class PayrollEdgeCaseTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public PayrollEdgeCaseTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Each test gets its own country so runs (unique per country/month) stay isolated.</summary>
    private async Task<string> CreateCountryAsync(
        string code, decimal employerRate = 0.12m, decimal deductionRate = 0.15m, string currency = "PHP")
    {
        var response = await _client.PostAsJsonAsync("/api/countries", new
        {
            code,
            name = $"Edgeland {code}",
            currencyCode = currency,
            employerCostRate = employerRate,
            employeeDeductionRate = deductionRate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return code.ToUpperInvariant();
    }

    private async Task<(Guid ClientId, Guid ContractId)> HireAsync(
        string prefix, string countryCode, decimal salary, string startDate = "2026-01-01",
        string? billingCurrency = null, decimal feeRate = 0.10m)
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = countryCode,
            billingCurrencyCode = billingCurrency,
            managementFeeRate = feeRate,
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
            monthlySalary = salary,
            startDate,
        });
        Assert.Equal(HttpStatusCode.Created, contractResponse.StatusCode);
        var contract = await contractResponse.Content.ReadFromJsonAsync<ContractResponse>();

        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });
        var activate = await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        return (client.Id, contract.Id);
    }

    private async Task<PayrollRunDetailResponse> CreateRunAsync(string country, int year, int month)
    {
        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year, month });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PayrollRunDetailResponse>())!;
    }

    private async Task<Guid> ApproveAmendmentAsync(Guid contractId, decimal newSalary, string effectiveDate)
    {
        var create = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId,
            newMonthlySalary = newSalary,
            effectiveDate,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var amendment = await create.Content.ReadFromJsonAsync<AmendmentResponse>();

        var approve = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendment!.Id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        return amendment.Id;
    }

    [Fact]
    public async Task ContractStartingOnLastDayOfMonth_IsPaidAFullMonth()
    {
        var country = await CreateCountryAsync("E1");
        await HireAsync("edgestart", country, 60_000m, startDate: "2026-03-31");

        var run = await CreateRunAsync(country, 2026, 3);

        // Documented simplification: coverage of any part of the month pays in full.
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(60_000m, slip.GrossSalary);
    }

    [Fact]
    public async Task ContractStartingNextMonth_IsNotPayableThisMonth()
    {
        var country = await CreateCountryAsync("E2");
        await HireAsync("edgefuture", country, 60_000m, startDate: "2026-04-01");

        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 3 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No payable contracts", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Amendment_EffectiveOnFirstDayOfMonth_PaysNewSalaryThatMonth()
    {
        var country = await CreateCountryAsync("E3");
        var (_, contractId) = await HireAsync("amendfirst", country, 50_000m);
        await ApproveAmendmentAsync(contractId, 65_000m, "2026-04-01");

        var march = await CreateRunAsync(country, 2026, 3);
        var april = await CreateRunAsync(country, 2026, 4);

        Assert.Equal(50_000m, Assert.Single(march.Payslips).GrossSalary);
        Assert.Equal(65_000m, Assert.Single(april.Payslips).GrossSalary);
    }

    [Fact]
    public async Task Amendment_EffectiveOnLastDayOfMonth_OwnsThatWholeMonth()
    {
        var country = await CreateCountryAsync("E4");
        var (_, contractId) = await HireAsync("amendlast", country, 50_000m);
        await ApproveAmendmentAsync(contractId, 65_000m, "2026-04-30");

        var april = await CreateRunAsync(country, 2026, 4);
        var march = await CreateRunAsync(country, 2026, 3);

        // Mid-month rule: the whole effective month pays the new salary, prior months the old.
        Assert.Equal(65_000m, Assert.Single(april.Payslips).GrossSalary);
        Assert.Equal(50_000m, Assert.Single(march.Payslips).GrossSalary);
    }

    [Fact]
    public async Task AmendmentApprovedAfterRunIsCreated_DoesNotRewriteTheDraftRun()
    {
        var country = await CreateCountryAsync("E5");
        var (clientId, contractId) = await HireAsync("racerun", country, 50_000m);

        // The run snapshots payslips first; the raise lands afterwards, effective
        // inside the already-computed period.
        var run = await CreateRunAsync(country, 2026, 3);
        await ApproveAmendmentAsync(contractId, 99_000m, "2026-03-01");

        var reloaded = await _client.GetFromJsonAsync<PayrollRunDetailResponse>($"/api/payroll-runs/{run.Run.Id}");
        Assert.Equal(50_000m, Assert.Single(reloaded!.Payslips).GrossSalary);

        // Completing the run invoices the snapshotted amounts, not the new salary.
        var complete = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await complete.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();
        var invoice = Assert.Single(completed!.Invoices);
        Assert.Equal(clientId, invoice.ClientId);
        Assert.Equal(56_000m + 5_000m, invoice.Total); // 50k + 12% employer cost + 10% fee on 50k

        // The next month picks the amended salary up.
        var april = await CreateRunAsync(country, 2026, 4);
        Assert.Equal(99_000m, Assert.Single(april.Payslips).GrossSalary);
    }

    [Fact]
    public async Task FxRate_EffectiveOnRunMonthEnd_IsApplied()
    {
        // A currency pair unique to this test: FX rates are global, so sharing a
        // pair with another test would leak rates across tests.
        var country = await CreateCountryAsync("E6", currency: "EUR");
        await HireAsync("fxmonthend", country, 100_000m, billingCurrency: "USD");
        var rate = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "EUR",
            quoteCurrencyCode = "USD",
            rate = 0.017m,
            effectiveDate = "2026-03-31",
        });
        Assert.Equal(HttpStatusCode.Created, rate.StatusCode);

        var run = await CreateRunAsync(country, 2026, 3);
        var complete = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await complete.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();
        var invoice = Assert.Single(completed!.Invoices);
        Assert.Equal(0.017m, invoice.FxRateApplied);
        // 100k gross -> 112k subtotal + 10k fee = 122k; * 0.017 = 2074.00 USD.
        Assert.Equal(2_074.00m, invoice.TotalInBillingCurrency);
    }

    [Fact]
    public async Task FxRate_EffectiveFirstDayAfterRunMonth_DoesNotCount_RunStaysDraft()
    {
        var country = await CreateCountryAsync("E7", currency: "GBP");
        await HireAsync("fxtoolate", country, 100_000m, billingCurrency: "USD");
        var rate = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "GBP",
            quoteCurrencyCode = "USD",
            rate = 0.017m,
            effectiveDate = "2026-04-01",
        });
        Assert.Equal(HttpStatusCode.Created, rate.StatusCode);

        var run = await CreateRunAsync(country, 2026, 3);
        var complete = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        Assert.Contains("No FX rate", await complete.Content.ReadAsStringAsync());

        var reloaded = await _client.GetFromJsonAsync<PayrollRunDetailResponse>($"/api/payroll-runs/{run.Run.Id}");
        Assert.Equal("Draft", reloaded!.Run.Status);
    }

    [Fact]
    public async Task FinalPayroll_TerminationOnLeapDay_PaysTheFullFebruary()
    {
        var country = await CreateCountryAsync("E8", employerRate: 0m, deductionRate: 0m);
        var (_, contractId) = await HireAsync("leapfull", country, 29_000m, startDate: "2027-01-01");
        var terminate = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate = "2028-02-29",
            reason = "Contract ended",
        });
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        var run = await CreateRunAsync(country, 2028, 2);

        // Ending on Feb 29 of a leap year is a full final month: no proration.
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(29_000m, slip.GrossSalary);
    }

    [Fact]
    public async Task FinalPayroll_MidLeapFebruary_ProratesOverTwentyNineDays()
    {
        var country = await CreateCountryAsync("E9", employerRate: 0m, deductionRate: 0m);
        var (_, contractId) = await HireAsync("leapmid", country, 29_000m, startDate: "2027-01-01");
        var terminate = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate = "2028-02-15",
            reason = "Contract ended",
        });
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        var run = await CreateRunAsync(country, 2028, 2);

        // 29000 * 15/29 = 15000 exactly — the divisor must be 29, not 28.
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(15_000m, slip.GrossSalary);
    }

    [Theory]
    [InlineData(2000, 1)]
    [InlineData(2100, 12)]
    public async Task CreateRun_YearBoundaries_AreAccepted(int year, int month)
    {
        var code = year == 2000 ? "EA" : "EB";
        var country = await CreateCountryAsync(code);
        await HireAsync($"yearedge{year}", country, 10_000m, startDate: "1999-01-01");

        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year, month });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(1999, 12)]
    [InlineData(2101, 1)]
    public async Task CreateRun_YearOutsideBounds_ReturnsValidationProblem(int year, int month)
    {
        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "PH", year, month });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CompletedInvoice_TotalInBillingCurrency_IsRoundedOnceOnTheTotal()
    {
        var country = await CreateCountryAsync("EC", currency: "AUD");
        await HireAsync("fxround", country, 33_333.33m, billingCurrency: "USD", feeRate: 0m);
        var rate = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "AUD",
            quoteCurrencyCode = "USD",
            rate = 0.017321m,
            effectiveDate = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, rate.StatusCode);

        var run = await CreateRunAsync(country, 2026, 3);
        var complete = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await complete.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = Assert.Single(completed!.Invoices);
        // Subtotal: 33333.33 gross + 4000.00 employer cost (12% of gross, rounded once).
        Assert.Equal(37_333.33m, invoice.PayrollSubtotal);
        Assert.Equal(0m, invoice.ManagementFee);
        // 37333.33 * 0.017321 = 646.65051... -> 646.65, rounded once on the total.
        Assert.Equal(646.65m, invoice.TotalInBillingCurrency);
    }
}
