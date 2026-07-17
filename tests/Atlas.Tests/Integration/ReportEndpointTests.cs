using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class ReportEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ReportEndpointTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        EnsureCountry("PH");
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

    private async Task<ClientResponse> CreateClientCompanyAsync(string name, string countryCode = "PH")
    {
        var response = await _client.PostAsJsonAsync("/api/clients", new
        {
            name,
            billingEmail = $"{name.ToLowerInvariant().Replace(' ', '.')}@example.com",
            headquartersCountryCode = countryCode,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!;
    }

    private async Task<WorkerResponse> CreateWorkerAsync(string emailPrefix, string countryCode = "PH")
    {
        var response = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{emailPrefix} Worker",
            email = $"{emailPrefix}.worker@example.com",
            countryCode,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkerResponse>())!;
    }

    private async Task<ContractResponse> CreateContractAsync(
        Guid clientId, Guid workerId, string startDate, bool activate = true)
    {
        var response = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
            workerId,
            jobTitle = "Engineer",
            monthlySalary = 100000,
            startDate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var contract = (await response.Content.ReadFromJsonAsync<ContractResponse>())!;

        if (activate)
        {
            _factory.WithDb(db =>
            {
                foreach (var item in db.OnboardingItems.Where(i => i.ContractId == contract.Id && i.IsRequired && !i.IsCompleted))
                {
                    item.Complete(DateTimeOffset.UtcNow);
                }
                db.SaveChanges();
            });
            var activateResponse = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        }

        return contract;
    }

    private async Task TerminateAsync(Guid contractId, string endDate)
    {
        var response = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate,
            reason = "Report setup",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Headcount_CountsEmployedContracts_AsOfDate()
    {
        EnsureCountry("QN");
        var alpha = await CreateClientCompanyAsync("Alpha Headcount Co", "QN");
        var beta = await CreateClientCompanyAsync("Beta Headcount Co", "QN");

        // Two active as of 2025-06-15, both started 2025-01-01.
        var active1 = await CreateContractAsync(alpha.Id, (await CreateWorkerAsync("hc1", "QN")).Id, "2025-01-01");
        var active2 = await CreateContractAsync(beta.Id, (await CreateWorkerAsync("hc2", "QN")).Id, "2025-01-01");
        // Terminated before the as-of date: excluded.
        var gone = await CreateContractAsync(alpha.Id, (await CreateWorkerAsync("hc3", "QN")).Id, "2025-01-01");
        await TerminateAsync(gone.Id, "2025-03-31");
        // Terminated but still serving notice on the as-of date: included.
        var leaving = await CreateContractAsync(alpha.Id, (await CreateWorkerAsync("hc4", "QN")).Id, "2025-01-01");
        await TerminateAsync(leaving.Id, "2025-12-31");
        // Draft: excluded.
        await CreateContractAsync(beta.Id, (await CreateWorkerAsync("hc5", "QN")).Id, "2025-01-01", activate: false);

        var report = await _client.GetFromJsonAsync<HeadcountReportResponse>(
            "/api/reports/headcount?asOf=2025-06-15");

        Assert.Equal(3, report!.Total);
        var qn = Assert.Single(report.ByCountry, r => r.CountryCode == "QN");
        Assert.Equal(3, qn.Count);
        Assert.Equal(2, Assert.Single(report.ByClient, r => r.ClientId == alpha.Id).Count);
        Assert.Equal(1, Assert.Single(report.ByClient, r => r.ClientId == beta.Id).Count);

        // Before anyone started: nothing employed.
        var before = await _client.GetFromJsonAsync<HeadcountReportResponse>(
            "/api/reports/headcount?asOf=2024-12-31");
        Assert.Equal(0, before!.Total);
    }

    [Fact]
    public async Task PayrollCosts_AggregatesPerMonth_WithFilters()
    {
        EnsureCountry("QO");
        var gamma = await CreateClientCompanyAsync("Gamma Cost Co", "QO");
        var delta = await CreateClientCompanyAsync("Delta Cost Co", "QO");
        await CreateContractAsync(gamma.Id, (await CreateWorkerAsync("pc1", "QO")).Id, "2026-01-01");
        await CreateContractAsync(delta.Id, (await CreateWorkerAsync("pc2", "QO")).Id, "2026-01-01");

        foreach (var month in new[] { 7, 8 })
        {
            var run = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QO", year = 2026, month });
            Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        }

        var rows = await _client.GetFromJsonAsync<List<PayrollCostRowResponse>>(
            "/api/reports/payroll-costs?countryCode=QO");
        Assert.Equal(2, rows!.Count);
        var july = rows.Single(r => r.Month == 7);
        Assert.Equal(2, july.PayslipCount);
        Assert.Equal(200000m, july.TotalGross);
        Assert.Equal(24000m, july.TotalEmployerCost);
        Assert.Equal(224000m, july.TotalCost);
        Assert.Equal("PHP", july.CurrencyCode);

        // Client filter narrows to one payslip per month.
        var gammaRows = await _client.GetFromJsonAsync<List<PayrollCostRowResponse>>(
            $"/api/reports/payroll-costs?countryCode=QO&clientId={gamma.Id}");
        Assert.All(gammaRows!, r => Assert.Equal(1, r.PayslipCount));

        // Period filter narrows to July only.
        var julyOnly = await _client.GetFromJsonAsync<List<PayrollCostRowResponse>>(
            "/api/reports/payroll-costs?countryCode=QO&fromYear=2026&fromMonth=7&toYear=2026&toMonth=7");
        var single = Assert.Single(julyOnly!);
        Assert.Equal(7, single.Month);
    }

    [Fact]
    public async Task ComplianceExpiries_ReturnsExpiredAndExpiringWithinWindow()
    {
        var worker = await CreateWorkerAsync("compliance-report");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task AddDocAsync(string name, DateOnly expiry)
        {
            var response = await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
            {
                type = "WorkPermit",
                name,
                issuedDate = today.AddYears(-1).ToString("yyyy-MM-dd"),
                expiryDate = expiry.ToString("yyyy-MM-dd"),
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await AddDocAsync("Expiring soon permit", today.AddDays(10));
        await AddDocAsync("Far future permit", today.AddDays(100));
        await AddDocAsync("Expired permit", today.AddDays(-5));

        var rows = await _client.GetFromJsonAsync<List<ComplianceExpiryRowResponse>>(
            "/api/reports/compliance-expiries?withinDays=30");

        var mine = rows!.Where(r => r.WorkerId == worker.Id).ToList();
        Assert.Equal(2, mine.Count);
        var expiring = mine.Single(r => r.Name == "Expiring soon permit");
        Assert.Equal(10, expiring.DaysUntilExpiry);
        Assert.Equal("ExpiringSoon", expiring.Status);
        var expired = mine.Single(r => r.Name == "Expired permit");
        Assert.Equal(-5, expired.DaysUntilExpiry);
        Assert.Equal("Expired", expired.Status);
        Assert.DoesNotContain(mine, r => r.Name == "Far future permit");
    }

    [Fact]
    public async Task InvoiceAging_BucketsByAgeSinceIssue()
    {
        EnsureCountry("QP");
        var epsilon = await CreateClientCompanyAsync("Epsilon Aging Co", "QP");
        await CreateContractAsync(epsilon.Id, (await CreateWorkerAsync("aging1", "QP")).Id, "2026-01-01");

        var invoiceIds = new List<Guid>();
        foreach (var month in new[] { 5, 6, 7 })
        {
            var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QP", year = 2026, month });
            Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
            var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
            var complete = await _client.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            var completed = await complete.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();
            invoiceIds.Add(completed!.Invoices.Single().Id);
        }

        // Age two of the three invoices: 45 and 100 days old.
        _factory.WithDb(db =>
        {
            db.Invoices.Single(i => i.Id == invoiceIds[0]).IssuedAtUtc = DateTimeOffset.UtcNow.AddDays(-100);
            db.Invoices.Single(i => i.Id == invoiceIds[1]).IssuedAtUtc = DateTimeOffset.UtcNow.AddDays(-45);
            db.SaveChanges();
        });

        var report = await _client.GetFromJsonAsync<InvoiceAgingReportResponse>(
            $"/api/reports/invoice-aging?clientId={epsilon.Id}");

        // Each invoice: 112000 subtotal + 10000 fee = 122000 PHP (billing currency = HQ currency).
        Assert.Equal(3, report!.Rows.Count);
        var current = report.Rows.Single(r => r.Bucket == "0-30");
        Assert.Equal(1, current.InvoiceCount);
        Assert.Equal(122000m, current.Total);
        Assert.Equal("PHP", current.CurrencyCode);
        Assert.Equal(1, report.Rows.Single(r => r.Bucket == "31-60").InvoiceCount);
        Assert.Equal(1, report.Rows.Single(r => r.Bucket == "90+").InvoiceCount);
        Assert.DoesNotContain(report.Rows, r => r.Bucket == "61-90");
    }

    [Fact]
    public async Task Reports_AsClientAdmin_Return403()
    {
        var clientCompany = await CreateClientCompanyAsync("Report Denied Co");
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Report denied admin",
            role = "ClientAdmin",
            clientId = clientCompany.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var clientAdmin = _factory.CreateClientWithApiKey(key);

        foreach (var path in new[]
        {
            "/api/reports/headcount",
            "/api/reports/payroll-costs",
            "/api/reports/compliance-expiries",
            "/api/reports/invoice-aging",
        })
        {
            var response = await clientAdmin.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
