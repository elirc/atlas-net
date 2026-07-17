using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;

namespace Atlas.Tests.Integration;

public class PayrollEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public PayrollEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Each test gets its own country so payroll runs (unique per country/month)
    /// and payslip counts are isolated from the other tests sharing this fixture.
    /// </summary>
    private async Task<string> CreateCountryAsync(
        string code, decimal employerRate = 0.12m, decimal deductionRate = 0.15m, string currency = "PHP")
    {
        var response = await _client.PostAsJsonAsync("/api/countries", new
        {
            code,
            name = $"Testland {code}",
            currencyCode = currency,
            employerCostRate = employerRate,
            employeeDeductionRate = deductionRate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return code.ToUpperInvariant();
    }

    /// <summary>Creates a client + worker + activated contract, returning ids.</summary>
    private async Task<(Guid ClientId, Guid ContractId)> HireAsync(
        string prefix, string countryCode, decimal salary, decimal feeRate = 0.10m, string startDate = "2026-01-01")
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = countryCode,
            managementFeeRate = feeRate,
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();

        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{prefix} Worker",
            email = $"{prefix}.worker@example.com",
            countryCode,
        });
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Engineer",
            monthlySalary = salary,
            startDate,
        });
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

    [Fact]
    public async Task CreateRun_ComputesPayslipsWithCorrectMoneyMath()
    {
        var country = await CreateCountryAsync("Q1");
        await HireAsync("payroll1a", country, 100_000m);
        await HireAsync("payroll1b", country, 50_000m);

        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = country.ToLowerInvariant(),
            year = 2026,
            month = 3,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal("Draft", detail.Run.Status);
        Assert.Equal(2, detail.Run.PayslipCount);
        Assert.Equal(150_000m, detail.Run.TotalGross);
        Assert.Equal(18_000m, detail.Run.TotalEmployerCost); // 12%
        Assert.Equal(127_500m, detail.Run.TotalNet); // gross - 15%
        Assert.Equal(168_000m, detail.Run.TotalCost);
        Assert.All(detail.Payslips, p => Assert.Equal("PHP", p.CurrencyCode));

        var bigSlip = detail.Payslips.Single(p => p.GrossSalary == 100_000m);
        Assert.Equal(12_000m, bigSlip.EmployerCost);
        Assert.Equal(15_000m, bigSlip.EmployeeDeductions);
        Assert.Equal(85_000m, bigSlip.NetPay);
        Assert.Equal(112_000m, bigSlip.TotalCost);
    }

    [Fact]
    public async Task CreateRun_NoContractCoversTheMonth_ReturnsConflict()
    {
        var country = await CreateCountryAsync("Q2");
        await HireAsync("payroll2", country, 6_000m, startDate: "2026-06-01");

        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = country,
            year = 2026,
            month = 1, // before the only contract starts
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No payable contracts", body);
    }

    [Fact]
    public async Task CreateRun_SameCountryAndMonthTwice_ReturnsConflict()
    {
        var country = await CreateCountryAsync("Q3");
        await HireAsync("payroll3", country, 80_000m);

        var first = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 4 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 4 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("QX", 1999, 5)]
    [InlineData("QX", 2026, 0)]
    [InlineData("QX", 2026, 13)]
    [InlineData("", 2026, 5)]
    public async Task CreateRun_InvalidInput_ReturnsValidationProblem(string country, int year, int month)
    {
        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = country,
            year,
            month,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRun_UnknownCountry_ReturnsConflictProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = "ZZ",
            year = 2026,
            month = 5,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body);
    }

    [Fact]
    public async Task CompleteRun_IssuesOneInvoicePerClientWithFee()
    {
        var country = await CreateCountryAsync("Q4");
        var (clientA, _) = await HireAsync("invoiceA", country, 100_000m, feeRate: 0.10m);
        var (clientB, _) = await HireAsync("invoiceB", country, 50_000m, feeRate: 0.08m);

        var create = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 5 });
        var detail = await create.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();

        var complete = await _client.PostAsync($"/api/payroll-runs/{detail!.Run.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await complete.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();
        Assert.Equal("Completed", completed!.Run.Status);
        Assert.NotNull(completed.Run.CompletedAtUtc);
        Assert.Equal(2, completed.Invoices.Count);

        var invoiceA = completed.Invoices.Single(i => i.ClientId == clientA);
        Assert.Equal(112_000m, invoiceA.PayrollSubtotal); // 100k + 12% employer cost
        Assert.Equal(10_000m, invoiceA.ManagementFee); // 10% of gross
        Assert.Equal(122_000m, invoiceA.Total);
        Assert.Equal("PHP", invoiceA.CurrencyCode);
        Assert.Matches($@"^INV-202605-{country}-\d{{3}}$", invoiceA.InvoiceNumber);

        var invoiceB = completed.Invoices.Single(i => i.ClientId == clientB);
        Assert.Equal(56_000m, invoiceB.PayrollSubtotal);
        Assert.Equal(4_000m, invoiceB.ManagementFee); // 8% of 50k
        Assert.Equal(60_000m, invoiceB.Total);

        // Invoices are queryable per client afterwards.
        var listed = await _client.GetFromJsonAsync<List<InvoiceResponse>>($"/api/invoices?clientId={clientA}");
        Assert.Single(listed!);
        Assert.Equal(invoiceA.InvoiceNumber, listed![0].InvoiceNumber);
    }

    [Fact]
    public async Task CompleteRun_Twice_ReturnsConflict()
    {
        var country = await CreateCountryAsync("Q5");
        await HireAsync("doublecomplete", country, 70_000m);
        var create = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 6 });
        var detail = await create.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();

        var first = await _client.PostAsync($"/api/payroll-runs/{detail!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsync($"/api/payroll-runs/{detail.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CompleteRun_UnknownRun_Returns404()
    {
        var response = await _client.PostAsync($"/api/payroll-runs/{Guid.NewGuid()}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TerminatedContract_IsPaidForTerminationMonth_ButNotAfter()
    {
        var country = await CreateCountryAsync("Q6", currency: "EUR");
        var (_, contractId) = await HireAsync("terminatedpay", country, 6_000m, startDate: "2026-01-01");
        var terminate = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate = "2026-02-15",
            reason = "Contract ended",
        });
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        var febRun = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 2 });
        Assert.Equal(HttpStatusCode.Created, febRun.StatusCode);
        var febDetail = await febRun.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        Assert.Contains(febDetail!.Payslips, p => p.ContractId == contractId);

        // March: the only contract in this country ended Feb 15 -> nothing to pay.
        var marRun = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 3 });
        Assert.Equal(HttpStatusCode.Conflict, marRun.StatusCode);
    }

    [Fact]
    public async Task ListRuns_FilterByCountry_ReturnsSummaries()
    {
        var country = await CreateCountryAsync("Q7");
        await HireAsync("listruns", country, 40_000m);
        await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 1 });
        await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = country, year = 2026, month = 2 });

        var runs = await _client.GetFromJsonAsync<List<PayrollRunSummaryResponse>>(
            $"/api/payroll-runs?countryCode={country}");

        Assert.NotNull(runs);
        Assert.Equal(2, runs.Count);
        Assert.Equal(1, runs[0].Month); // chronological order
        Assert.Equal(2, runs[1].Month);
        Assert.All(runs, r => Assert.Equal(country, r.CountryCode));
        Assert.All(runs, r => Assert.Equal(1, r.PayslipCount));
    }
}
