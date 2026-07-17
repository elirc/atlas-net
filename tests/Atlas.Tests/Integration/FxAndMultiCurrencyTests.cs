using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class FxAndMultiCurrencyTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public FxAndMultiCurrencyTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.WithDb(db =>
        {
            if (!db.Countries.Any(c => c.Code == "US"))
            {
                db.Countries.Add(new Country
                {
                    Code = "US",
                    Name = "United States",
                    CurrencyCode = "USD",
                    EmployerCostRate = 0.10m,
                    EmployeeDeductionRate = 0.22m,
                });
                db.SaveChanges();
            }
        });
    }

    /// <summary>Provisions a private payroll country (PHP-denominated) for run isolation.</summary>
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

    /// <summary>Creates a USD-billed client with one active contract in a PHP payroll country.</summary>
    private async Task<(ContractResponse Contract, ClientResponse Client)> CreateUsdClientWithActiveContractAsync(
        string prefix, string countryCode)
    {
        EnsureCountry(countryCode);
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = $"{prefix} Co",
            billingEmail = $"{prefix}@example.com",
            headquartersCountryCode = "US",
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

        return (contract, client);
    }

    private async Task AddRateAsync(string baseCode, string quoteCode, decimal rate, string effectiveDate)
    {
        var response = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = baseCode,
            quoteCurrencyCode = quoteCode,
            rate,
            effectiveDate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateFxRate_Duplicate_ReturnsConflict()
    {
        await AddRateAsync("AAA", "BBB", 1.5m, "2026-01-01");

        var response = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "AAA",
            quoteCurrencyCode = "BBB",
            rate = 1.6m,
            effectiveDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateFxRate_SameBaseAndQuote_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "USD",
            quoteCurrencyCode = "usd",
            rate = 1.0m,
            effectiveDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateFxRate_NonPositiveRate_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "CCC",
            quoteCurrencyCode = "DDD",
            rate = 0,
            effectiveDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateClient_DefaultsBillingCurrency_ToHeadquartersCurrency()
    {
        var response = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Default Currency Co",
            billingEmail = "defaultcurrency@example.com",
            headquartersCountryCode = "US",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var client = await response.Content.ReadFromJsonAsync<ClientResponse>();
        Assert.Equal("USD", client!.BillingCurrencyCode);
    }

    [Fact]
    public async Task CreateClient_ExplicitBillingCurrency_IsKept()
    {
        var response = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Euro Billed Co",
            billingEmail = "eurobilled@example.com",
            headquartersCountryCode = "US",
            billingCurrencyCode = "eur",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var client = await response.Content.ReadFromJsonAsync<ClientResponse>();
        Assert.Equal("EUR", client!.BillingCurrencyCode);
    }

    [Fact]
    public async Task CompleteRun_ConvertsInvoiceIntoBillingCurrency_AtPeriodRate()
    {
        var (_, client) = await CreateUsdClientWithActiveContractAsync("fxconvert", "QE");
        await AddRateAsync("PHP", "USD", 0.0170m, "2026-01-01");
        await AddRateAsync("PHP", "USD", 0.0180m, "2026-08-01"); // later rate must not apply to July

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QE", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var completeResponse = await _client.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = completed!.Invoices.Single(i => i.ClientId == client.Id);
        // Local: 100000 gross + 12000 employer cost = 112000 subtotal; fee 10% of gross = 10000; total 122000 PHP.
        Assert.Equal("PHP", invoice.CurrencyCode);
        Assert.Equal(122000m, invoice.Total);
        // Converted at the July rate (0.0170), not the August one.
        Assert.Equal("USD", invoice.BillingCurrencyCode);
        Assert.Equal(0.0170m, invoice.FxRateApplied);
        Assert.Equal(2074.00m, invoice.TotalInBillingCurrency); // 122000 * 0.0170
    }

    [Fact]
    public async Task CompleteRun_SameCurrency_UsesRateOfOne()
    {
        EnsureCountry("QF");
        // Client billed in PHP (explicit), payroll in PHP.
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Local Billed Co",
            billingEmail = "localbilled@example.com",
            headquartersCountryCode = "US",
            billingCurrencyCode = "PHP",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Local Billed Worker",
            email = "localbilled.worker@example.com",
            countryCode = "QF",
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
        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired && !i.IsCompleted))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });
        await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QF", year = 2026, month = 7 });
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var completeResponse = await _client.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = completed!.Invoices.Single(i => i.ClientId == client.Id);
        Assert.Equal("PHP", invoice.BillingCurrencyCode);
        Assert.Equal(1m, invoice.FxRateApplied);
        Assert.Equal(invoice.Total, invoice.TotalInBillingCurrency);
    }

    [Fact]
    public async Task CompleteRun_MissingRate_Returns409_AndRunStaysDraft()
    {
        EnsureCountry("QG");
        // Billed in a currency no rate exists for (PHP -> ZWL).
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "FX Missing Co",
            billingEmail = "fxmissing@example.com",
            headquartersCountryCode = "US",
            billingCurrencyCode = "ZWL",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "FX Missing Worker",
            email = "fxmissing.worker@example.com",
            countryCode = "QG",
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
        _factory.WithDb(db =>
        {
            foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract!.Id && i.IsRequired && !i.IsCompleted))
            {
                item.Complete(DateTimeOffset.UtcNow);
            }
            db.SaveChanges();
        });
        await _client.PostAsync($"/api/contracts/{contract!.Id}/activate", null);

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QG", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();

        var completeResponse = await _client.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, completeResponse.StatusCode);

        // Nothing was persisted: the run is still draft and can complete once a rate exists.
        var reloaded = await _client.GetFromJsonAsync<PayrollRunDetailResponse>($"/api/payroll-runs/{run.Run.Id}");
        Assert.Equal("Draft", reloaded!.Run.Status);

        await AddRateAsync("PHP", "ZWL", 0.0190m, "2026-07-01");
        var retry = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var completed = await retry.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();
        Assert.Equal(0.0190m, completed!.Invoices.Single().FxRateApplied);
    }

    [Fact]
    public async Task FxRates_AsClientAdmin_CannotCreate()
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "FX Denied Co",
            billingEmail = "fxdenied@example.com",
            headquartersCountryCode = "US",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "FX denied admin",
            role = "ClientAdmin",
            clientId = client!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        var response = await clientAdmin.PostAsJsonAsync("/api/fx-rates", new
        {
            baseCurrencyCode = "EEE",
            quoteCurrencyCode = "FFF",
            rate = 2.0m,
            effectiveDate = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Reading rates is allowed for any authenticated caller.
        var list = await clientAdmin.GetAsync("/api/fx-rates");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
