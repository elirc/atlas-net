using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

public class BenefitEndpointTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public BenefitEndpointTests(AtlasApiFactory factory)
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

    private async Task<ContractResponse> CreateActiveContractAsync(string prefix, string countryCode = "PH")
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

        return contract;
    }

    private async Task<BenefitPlanResponse> CreatePlanAsync(
        string name, string countryCode = "PH", decimal monthlyCost = 4500m, decimal employerRate = 0.80m)
    {
        var response = await _client.PostAsJsonAsync("/api/benefit-plans", new
        {
            countryCode,
            name,
            monthlyCost,
            employerContributionRate = employerRate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BenefitPlanResponse>())!;
    }

    private async Task<BenefitEnrollmentResponse> EnrollAsync(Guid contractId, Guid planId, string startDate)
    {
        var response = await _client.PostAsJsonAsync("/api/benefit-enrollments", new
        {
            contractId,
            benefitPlanId = planId,
            startDate,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BenefitEnrollmentResponse>())!;
    }

    [Fact]
    public async Task CreatePlan_ComputesShares()
    {
        var plan = await CreatePlanAsync("Shares Plan");

        Assert.Equal(3600.00m, plan.EmployerShare);
        Assert.Equal(900.00m, plan.EmployeeShare);
        Assert.True(plan.IsActive);
    }

    [Fact]
    public async Task CreatePlan_DuplicateNameInCountry_ReturnsConflict()
    {
        await CreatePlanAsync("Duplicate Plan");

        var response = await _client.PostAsJsonAsync("/api/benefit-plans", new
        {
            countryCode = "PH",
            name = "Duplicate Plan",
            monthlyCost = 1000,
            employerContributionRate = 0.5,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_RateAboveOne_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/benefit-plans", new
        {
            countryCode = "PH",
            name = "Bad Rate Plan",
            monthlyCost = 1000,
            employerContributionRate = 1.5,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Enroll_ActiveContractInSameCountry_Succeeds()
    {
        var contract = await CreateActiveContractAsync("enroll");
        var plan = await CreatePlanAsync("Enroll Plan");

        var enrollment = await EnrollAsync(contract.Id, plan.Id, "2026-02-01");

        Assert.Equal("Active", enrollment.Status);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal("Enroll Plan", enrollment.BenefitPlanName);
    }

    [Fact]
    public async Task Enroll_PlanFromAnotherCountry_ReturnsConflict()
    {
        EnsureCountry("QH");
        var contract = await CreateActiveContractAsync("wrongcountry"); // PH contract
        var plan = await CreatePlanAsync("Foreign Plan", countryCode: "QH");

        var response = await _client.PostAsJsonAsync("/api/benefit-enrollments", new
        {
            contractId = contract.Id,
            benefitPlanId = plan.Id,
            startDate = "2026-02-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Enroll_TwiceInSamePlan_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("doubleenroll");
        var plan = await CreatePlanAsync("Double Enroll Plan");
        await EnrollAsync(contract.Id, plan.Id, "2026-02-01");

        var response = await _client.PostAsJsonAsync("/api/benefit-enrollments", new
        {
            contractId = contract.Id,
            benefitPlanId = plan.Id,
            startDate = "2026-03-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Enroll_InDeactivatedPlan_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("inactiveplan");
        var plan = await CreatePlanAsync("Sunset Plan");
        var deactivate = await _client.PostAsync($"/api/benefit-plans/{plan.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await _client.PostAsJsonAsync("/api/benefit-enrollments", new
        {
            contractId = contract.Id,
            benefitPlanId = plan.Id,
            startDate = "2026-02-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task EndEnrollment_BeforeStart_ReturnsConflict()
    {
        var contract = await CreateActiveContractAsync("endearly");
        var plan = await CreatePlanAsync("End Early Plan");
        var enrollment = await EnrollAsync(contract.Id, plan.Id, "2026-02-01");

        var response = await _client.PostAsJsonAsync($"/api/benefit-enrollments/{enrollment.Id}/end", new
        {
            endDate = "2026-01-15",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task EndEnrollment_ThenReenroll_Succeeds()
    {
        var contract = await CreateActiveContractAsync("reenroll");
        var plan = await CreatePlanAsync("Reenroll Plan");
        var enrollment = await EnrollAsync(contract.Id, plan.Id, "2026-02-01");

        var endResponse = await _client.PostAsJsonAsync($"/api/benefit-enrollments/{enrollment.Id}/end", new
        {
            endDate = "2026-06-30",
        });
        Assert.Equal(HttpStatusCode.OK, endResponse.StatusCode);
        var ended = await endResponse.Content.ReadFromJsonAsync<BenefitEnrollmentResponse>();
        Assert.Equal("Ended", ended!.Status);
        Assert.Equal(new DateOnly(2026, 6, 30), ended.EndDate);

        var again = await EnrollAsync(contract.Id, plan.Id, "2026-09-01");
        Assert.Equal("Active", again.Status);
    }

    [Fact]
    public async Task Payroll_ChargesBenefits_OnlyForCoveredMonths()
    {
        var contract = await CreateActiveContractAsync("benefitpay", countryCode: "QI");
        var plan = await CreatePlanAsync("Payroll Plan", countryCode: "QI", monthlyCost: 4500m, employerRate: 0.80m);
        var enrollment = await EnrollAsync(contract.Id, plan.Id, "2026-07-01");
        await _client.PostAsJsonAsync($"/api/benefit-enrollments/{enrollment.Id}/end", new { endDate = "2026-08-31" });

        // June: not yet covered.
        var june = await CreateRunAsync("QI", 2026, 6);
        var juneSlip = june.Payslips.Single(p => p.ContractId == contract.Id);
        Assert.Equal(0m, juneSlip.BenefitsEmployerCost);
        Assert.Equal(0m, juneSlip.BenefitsEmployeeDeduction);
        Assert.Equal(85000m, juneSlip.NetPay);   // 100000 - 15% deductions
        Assert.Equal(112000m, juneSlip.TotalCost);

        // July: covered. Employer 3600 billed, employee 900 withheld.
        var july = await CreateRunAsync("QI", 2026, 7);
        var julySlip = july.Payslips.Single(p => p.ContractId == contract.Id);
        Assert.Equal(3600m, julySlip.BenefitsEmployerCost);
        Assert.Equal(900m, julySlip.BenefitsEmployeeDeduction);
        Assert.Equal(85000m - 900m, julySlip.NetPay);
        Assert.Equal(112000m + 3600m, julySlip.TotalCost);

        // September: coverage ended 2026-08-31.
        var september = await CreateRunAsync("QI", 2026, 9);
        var septemberSlip = september.Payslips.Single(p => p.ContractId == contract.Id);
        Assert.Equal(0m, septemberSlip.BenefitsEmployerCost);
        Assert.Equal(85000m, septemberSlip.NetPay);
    }

    [Fact]
    public async Task Invoice_IncludesEmployerBenefitShare_NotEmployeeShare()
    {
        var contract = await CreateActiveContractAsync("benefitbill", countryCode: "QJ");
        var plan = await CreatePlanAsync("Billing Plan", countryCode: "QJ", monthlyCost: 4500m, employerRate: 0.80m);
        await EnrollAsync(contract.Id, plan.Id, "2026-07-01");

        var run = await CreateRunAsync("QJ", 2026, 7);
        var completeResponse = await _client.PostAsync($"/api/payroll-runs/{run.Run.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<PayrollRunCompletedResponse>();

        var invoice = completed!.Invoices.Single();
        // Subtotal: 100000 gross + 12000 employer cost + 3600 employer benefits = 115600.
        Assert.Equal(115600m, invoice.PayrollSubtotal);
        Assert.Equal(10000m, invoice.ManagementFee); // 10% of gross only
        Assert.Equal(125600m, invoice.Total);
    }

    [Fact]
    public async Task Enrollments_AsOtherClientsViewer_AreHidden()
    {
        var contract = await CreateActiveContractAsync("benefitscope");
        var plan = await CreatePlanAsync("Scope Plan");
        var enrollment = await EnrollAsync(contract.Id, plan.Id, "2026-02-01");

        var otherClientResponse = await _client.PostAsJsonAsync("/api/clients", new
        {
            name = "Benefit Outsider Co",
            billingEmail = "benefitoutsider@example.com",
            headquartersCountryCode = "PH",
        });
        var otherClient = await otherClientResponse.Content.ReadFromJsonAsync<ClientResponse>();
        var keyResponse = await _client.PostAsJsonAsync("/api/api-users", new
        {
            name = "Benefit outsider viewer",
            role = "ClientViewer",
            clientId = otherClient!.Id,
        });
        var key = (await keyResponse.Content.ReadFromJsonAsync<ApiUserCreatedResponse>())!.ApiKey;
        var outsider = _factory.CreateClientWithApiKey(key);

        var list = await outsider.GetFromJsonAsync<List<BenefitEnrollmentResponse>>("/api/benefit-enrollments");
        var direct = await outsider.GetAsync($"/api/benefit-enrollments/{enrollment.Id}");
        var end = await outsider.PostAsJsonAsync($"/api/benefit-enrollments/{enrollment.Id}/end", new { endDate = "2026-12-31" });

        Assert.DoesNotContain(list!, e => e.Id == enrollment.Id);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, end.StatusCode);

        // Plans themselves are readable by any authenticated user, but not creatable.
        var planList = await outsider.GetAsync("/api/benefit-plans");
        Assert.Equal(HttpStatusCode.OK, planList.StatusCode);
        var planCreate = await outsider.PostAsJsonAsync("/api/benefit-plans", new
        {
            countryCode = "PH",
            name = "Rogue Plan",
            monthlyCost = 1,
            employerContributionRate = 0,
        });
        Assert.Equal(HttpStatusCode.Forbidden, planCreate.StatusCode);
    }

    private async Task<PayrollRunDetailResponse> CreateRunAsync(string countryCode, int year, int month)
    {
        var response = await _client.PostAsJsonAsync("/api/payroll-runs", new { countryCode, year, month });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PayrollRunDetailResponse>())!;
    }
}
