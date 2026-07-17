using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;

namespace Atlas.Tests.Integration;

/// <summary>
/// One end-to-end EOR journey exercised purely through the public API:
/// support a country, sign a client, hire a worker, onboard, activate,
/// file compliance documents, run payroll, complete it, and invoice the client.
/// </summary>
public class FullJourneyTests : IClassFixture<AtlasApiFactory>
{
    private readonly HttpClient _client;

    public FullJourneyTests(AtlasApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CompleteEorLifecycle_FromCountrySetupToInvoice()
    {
        // 1. Atlas starts supporting a new country.
        var countryResponse = await _client.PostAsJsonAsync("/api/countries", new
        {
            code = "JP",
            name = "Japan",
            currencyCode = "JPY",
            employerCostRate = 0.155,
            employeeDeductionRate = 0.20,
        });
        Assert.Equal(HttpStatusCode.Created, countryResponse.StatusCode);

        // 2. A client signs up.
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Umbrella Digital",
            legalName = "Umbrella Digital K.K.",
            billingEmail = "finance@umbrella.example",
            headquartersCountryCode = "JP",
            managementFeeRate = 0.10,
        });
        Assert.Equal(HttpStatusCode.Created, clientResponse.StatusCode);
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();

        // 3. They want to hire a worker in Japan.
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Yuki Tanaka",
            email = "yuki.tanaka@example.com",
            countryCode = "JP",
            dateOfBirth = "1992-03-08",
        });
        Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        // 4. A draft contract is issued in the local currency.
        var contractResponse = await _client.PostAsJsonAsync("/api/contracts", new
        {
            clientId = client!.Id,
            workerId = worker!.Id,
            jobTitle = "Platform Engineer",
            monthlySalary = 800_000,
            startDate = "2026-04-01",
        });
        Assert.Equal(HttpStatusCode.Created, contractResponse.StatusCode);
        var contract = await contractResponse.Content.ReadFromJsonAsync<ContractResponse>();
        Assert.Equal("JPY", contract!.CurrencyCode);
        Assert.Equal("Draft", contract.Status);

        // 5. Activation is blocked until onboarding is done.
        var earlyActivate = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, earlyActivate.StatusCode);

        var checklist = await _client.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        foreach (var item in checklist!.Items.Where(i => i.IsRequired))
        {
            var complete = await _client.PostAsJsonAsync(
                $"/api/contracts/{contract.Id}/onboarding/{item.Id}/complete",
                new { notes = "Verified" });
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        // 6. Compliance documents are filed along the way.
        var docResponse = await _client.PostAsJsonAsync($"/api/workers/{worker.Id}/documents", new
        {
            type = "WorkPermit",
            name = "Japan work visa",
            issuedDate = "2026-03-01",
            expiryDate = "2027-03-01",
        });
        Assert.Equal(HttpStatusCode.Created, docResponse.StatusCode);

        // 7. Now activation succeeds.
        var activate = await _client.PostAsync($"/api/contracts/{contract.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        // 8. April payroll is run for Japan.
        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = "JP",
            year = 2026,
            month = 4,
        });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();

        var payslip = Assert.Single(run!.Payslips);
        Assert.Equal(800_000m, payslip.GrossSalary);
        Assert.Equal(124_000m, payslip.EmployerCost); // 15.5%
        Assert.Equal(160_000m, payslip.EmployeeDeductions); // 20%
        Assert.Equal(640_000m, payslip.NetPay);
        Assert.Equal(924_000m, payslip.TotalCost);

        // 9. Completing the run issues the client invoice.
        var completeRun = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeRun.StatusCode);
        var completed = await completeRun.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = Assert.Single(completed!.Invoices);
        Assert.Equal(client.Id, invoice.ClientId);
        Assert.Equal("JPY", invoice.CurrencyCode);
        Assert.Equal(924_000m, invoice.PayrollSubtotal);
        Assert.Equal(80_000m, invoice.ManagementFee); // 10% of gross
        Assert.Equal(1_004_000m, invoice.Total);
        Assert.Equal("INV-202604-JP-001", invoice.InvoiceNumber);

        // 10. The journey is visible through the read APIs.
        var invoices = await _client.GetFromJsonAsync<List<InvoiceResponse>>($"/api/invoices?clientId={client.Id}");
        Assert.Single(invoices!);

        var runs = await _client.GetFromJsonAsync<List<PayrollRunSummaryResponse>>("/api/payroll-runs?countryCode=JP");
        Assert.Single(runs!);
        Assert.Equal("Completed", runs![0].Status);
    }
}
