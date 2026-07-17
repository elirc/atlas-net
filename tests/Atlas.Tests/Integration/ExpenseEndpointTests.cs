using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class ExpenseEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ExpenseEndpointTests(AtlasApiFactory factory)
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

    /// <summary>Provisions a private country so payroll-run uniqueness never leaks between tests.</summary>
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

    private async Task<(ContractResponse Contract, Guid ClientId)> CreateActiveContractAsync(
        string prefix, string countryCode = "PH")
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

        return (contract, client.Id);
    }

    private async Task<ExpenseClaimResponse> SubmitClaimAsync(Guid contractId, params object[] items)
    {
        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId,
            description = "Business trip",
            items,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpenseClaimResponse>())!;
    }

    private static object Item(decimal amount, string description = "Taxi", string? receiptUrl = null) => new
    {
        description,
        amount,
        incurredDate = "2026-07-01",
        receiptUrl,
    };

    [Fact]
    public async Task CreateClaim_WithItems_ComputesTotalAndStartsPending()
    {
        var (contract, _) = await CreateActiveContractAsync("submit");

        var claim = await SubmitClaimAsync(
            contract.Id,
            Item(350.50m, "Taxi", "https://receipts.example.com/taxi.pdf"),
            Item(1200m, "Team lunch"));

        Assert.Equal("Pending", claim.Status);
        Assert.Equal(1550.50m, claim.TotalAmount);
        Assert.Equal("PHP", claim.CurrencyCode);
        Assert.Equal(2, claim.Items.Count);
        Assert.Contains(claim.Items, i => i.ReceiptUrl == "https://receipts.example.com/taxi.pdf");
    }

    [Fact]
    public async Task CreateClaim_WithoutItems_ReturnsValidationProblem()
    {
        var (contract, _) = await CreateActiveContractAsync("noitems");

        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = contract.Id,
            items = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateClaim_NonPositiveAmount_ReturnsValidationProblem()
    {
        var (contract, _) = await CreateActiveContractAsync("zeroamount");

        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = contract.Id,
            items = new[] { Item(0m) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateClaim_InvalidReceiptUrl_ReturnsValidationProblem()
    {
        var (contract, _) = await CreateActiveContractAsync("badurl");

        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = contract.Id,
            items = new[] { Item(100m, "Taxi", "not-a-url") },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateClaim_OnDraftContract_ReturnsConflict()
    {
        var clientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Draft Expense Co",
            billingEmail = "draftexpense@example.com",
            headquartersCountryCode = "PH",
        });
        var client = await clientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var workerResponse = await _client.PostAsJsonAsync("/api/workers", new
        {
            fullName = "Draft Expense Worker",
            email = "draftexpense.worker@example.com",
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

        var response = await _client.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = contract!.Id,
            items = new[] { Item(100m) },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ApproveClaim_Pending_BecomesApproved()
    {
        var (contract, _) = await CreateActiveContractAsync("approveclaim");
        var claim = await SubmitClaimAsync(contract.Id, Item(500m));

        var response = await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new
        {
            note = "Receipts verified",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<ExpenseClaimResponse>();
        Assert.Equal("Approved", approved!.Status);
        Assert.Equal("Receipts verified", approved.DecisionNote);
    }

    [Fact]
    public async Task ApproveClaim_Twice_ReturnsConflict()
    {
        var (contract, _) = await CreateActiveContractAsync("doubleclaim");
        var claim = await SubmitClaimAsync(contract.Id, Item(500m));
        await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new { });

        var second = await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task RejectClaim_WithoutNote_ReturnsConflict()
    {
        var (contract, _) = await CreateActiveContractAsync("rejectclaim");
        var claim = await SubmitClaimAsync(contract.Id, Item(500m));

        var response = await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/reject", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PayrollRun_ReimbursesApprovedClaims_AndSkipsOthers()
    {
        var (contract, _) = await CreateActiveContractAsync("reimburse", countryCode: "QA");

        var approved = await SubmitClaimAsync(contract.Id, Item(350.50m), Item(1200m)); // 1550.50
        await _client.PostAsJsonAsync($"/api/expense-claims/{approved.Id}/approve", new { });

        var rejected = await SubmitClaimAsync(contract.Id, Item(999m));
        await _client.PostAsJsonAsync($"/api/expense-claims/{rejected.Id}/reject", new { note = "No receipt" });

        var pending = await SubmitClaimAsync(contract.Id, Item(777m)); // stays pending

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new
        {
            countryCode = "QA",
            year = 2026,
            month = 7,
        });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();

        // 100000 gross, 12% employer, 15% deductions + 1550.50 reimbursed.
        var payslip = run!.Payslips.Single(p => p.ContractId == contract.Id);
        Assert.Equal(1550.50m, payslip.Reimbursements);
        Assert.Equal(85000m + 1550.50m, payslip.NetPay);
        Assert.Equal(112000m + 1550.50m, payslip.TotalCost);

        var reimbursedClaim = await _client.GetFromJsonAsync<ExpenseClaimResponse>($"/api/expense-claims/{approved.Id}");
        Assert.Equal("Reimbursed", reimbursedClaim!.Status);
        Assert.Equal(run.Run.Id, reimbursedClaim.ReimbursedInPayrollRunId);

        var stillPending = await _client.GetFromJsonAsync<ExpenseClaimResponse>($"/api/expense-claims/{pending.Id}");
        Assert.Equal("Pending", stillPending!.Status);
        var stillRejected = await _client.GetFromJsonAsync<ExpenseClaimResponse>($"/api/expense-claims/{rejected.Id}");
        Assert.Equal("Rejected", stillRejected!.Status);
    }

    [Fact]
    public async Task PayrollRun_DoesNotReimburseTheSameClaimTwice()
    {
        var (contract, _) = await CreateActiveContractAsync("once", countryCode: "QB");
        var claim = await SubmitClaimAsync(contract.Id, Item(1000m));
        await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new { });

        var firstRun = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QB", year = 2026, month = 7 });
        Assert.Equal(HttpStatusCode.Created, firstRun.StatusCode);
        var first = await firstRun.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        Assert.Equal(1000m, first!.Payslips.Single(p => p.ContractId == contract.Id).Reimbursements);

        var secondRun = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QB", year = 2026, month = 8 });
        Assert.Equal(HttpStatusCode.Created, secondRun.StatusCode);
        var second = await secondRun.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        Assert.Equal(0m, second!.Payslips.Single(p => p.ContractId == contract.Id).Reimbursements);
    }

    [Fact]
    public async Task Invoice_IncludesReimbursements_WithoutManagementFeeOnThem()
    {
        var (contract, clientId) = await CreateActiveContractAsync("billing", countryCode: "QC");
        var claim = await SubmitClaimAsync(contract.Id, Item(2000m));
        await _client.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new { });

        var runResponse = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode = "QC", year = 2026, month = 7 });
        var run = await runResponse.Content.ReadFromJsonAsync<PayrollRunDetailResponse>();
        var completeResponse = await _client.PostAsync($"/api/payroll-runs/{run!.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = completed!.Invoices.Single(i => i.ClientId == clientId);
        // Subtotal: 100000 gross + 12000 employer cost + 2000 reimbursed = 114000.
        Assert.Equal(114000m, invoice.PayrollSubtotal);
        // Management fee (10%) applies to gross only, not reimbursements.
        Assert.Equal(10000m, invoice.ManagementFee);
        Assert.Equal(124000m, invoice.Total);
    }

    [Fact]
    public async Task ExpenseClaims_AsOtherClientsViewer_AreHidden()
    {
        var (contract, _) = await CreateActiveContractAsync("expscope");
        var claim = await SubmitClaimAsync(contract.Id, Item(500m));

        var otherClientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Expense Outsider Co",
            billingEmail = "expenseoutsider@example.com",
            headquartersCountryCode = "PH",
        });
        var otherClient = await otherClientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Expense outsider viewer",
            role = "ClientViewer",
            clientId = otherClient!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var outsider = _factory.CreateClientWithApiKey(key);

        var list = await outsider.GetFromJsonAsync<List<ExpenseClaimResponse>>("/api/expense-claims");
        var direct = await outsider.GetAsync($"/api/expense-claims/{claim.Id}");
        var decide = await outsider.PostAsJsonAsync($"/api/expense-claims/{claim.Id}/approve", new { });

        Assert.DoesNotContain(list!, c => c.Id == claim.Id);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, decide.StatusCode);
    }
}
