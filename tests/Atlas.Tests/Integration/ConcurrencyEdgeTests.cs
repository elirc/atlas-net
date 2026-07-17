using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests.Integration;

/// <summary>
/// Race outcomes: stale optimistic-concurrency writes fail with the Version
/// token, and the loser of any double-decision sequence gets a 409 whose
/// ProblemDetails names the state that beat it. Complements
/// ProductionReadinessTests.ConcurrentUpdates_FailWithStaleVersion.
/// </summary>
public class ConcurrencyEdgeTests : IClassFixture<AtlasApiFactory>
{
    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _client;

    public ConcurrencyEdgeTests(AtlasApiFactory factory)
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

    private async Task<Guid> CreateActiveContractAsync(string prefix)
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
        return contract.Id;
    }

    private async Task<Guid> CreatePendingAmendmentAsync(Guid contractId, decimal salary)
    {
        var response = await _client.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId,
            newMonthlySalary = salary,
            effectiveDate = "2026-09-01",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AmendmentResponse>())!.Id;
    }

    private sealed record ProblemBody(string? Title, int Status, string Detail);

    [Fact]
    public async Task StaleAmendmentWrite_FailsOnVersionToken_AndTheFirstDecisionStands()
    {
        var contractId = await CreateActiveContractAsync("staleamend");
        var amendmentId = await CreatePendingAmendmentAsync(contractId, 110_000m);

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var amendmentA = await dbA.ContractAmendments.SingleAsync(a => a.Id == amendmentId);
        var amendmentB = await dbB.ContractAmendments.SingleAsync(a => a.Id == amendmentId);

        amendmentA.Reject("Budget freeze", DateTimeOffset.UtcNow);
        await dbA.SaveChangesAsync();

        amendmentB.Approve(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        var final = await _client.GetFromJsonAsync<AmendmentResponse>($"/api/contract-amendments/{amendmentId}");
        Assert.Equal("Rejected", final!.Status);

        // The rejected raise never reached the contract.
        var contract = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contractId}");
        Assert.Equal(100_000m, contract!.MonthlySalary);
    }

    [Fact]
    public async Task StaleContractWrite_FailsOnVersionToken()
    {
        var contractId = await CreateActiveContractAsync("stalecontract");

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var contractA = await dbA.Contracts.SingleAsync(c => c.Id == contractId);
        var contractB = await dbB.Contracts.SingleAsync(c => c.Id == contractId);

        contractA.Terminate(new DateOnly(2026, 12, 31), "First writer", DateTimeOffset.UtcNow);
        await dbA.SaveChangesAsync();

        contractB.Terminate(new DateOnly(2026, 6, 30), "Second writer", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        var final = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contractId}");
        Assert.Equal(new DateOnly(2026, 12, 31), final!.EndDate);
        Assert.Equal("First writer", final.TerminationReason);
    }

    [Fact]
    public async Task DoubleApproval_SecondLoses_WithExactProblemDetail()
    {
        var contractId = await CreateActiveContractAsync("doubleapprove");
        var amendmentId = await CreatePendingAmendmentAsync(contractId, 120_000m);

        var first = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendmentId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendmentId}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal(409, problem!.Status);
        Assert.Equal("Only pending amendments can be approved; this amendment is Approved.", problem.Detail);

        // The double approval must not append a second salary record.
        var history = await _client.GetFromJsonAsync<List<SalaryRecordResponse>>(
            $"/api/contracts/{contractId}/salary-history");
        Assert.Equal(2, history!.Count); // initial + one amendment
        Assert.Single(history, r => r.Source == "Amendment");
    }

    [Fact]
    public async Task ApproveAfterCancel_Loses_WithExactProblemDetail()
    {
        var contractId = await CreateActiveContractAsync("cancelrace");
        var amendmentId = await CreatePendingAmendmentAsync(contractId, 130_000m);

        var cancel = await _client.PostAsync($"/api/contract-amendments/{amendmentId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var approve = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendmentId}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var problem = await approve.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Only pending amendments can be approved; this amendment is Cancelled.", problem!.Detail);

        // The cancelled raise never landed.
        var contract = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contractId}");
        Assert.Equal(100_000m, contract!.MonthlySalary);
    }

    [Fact]
    public async Task RejectAfterApprove_OnLeaveRequest_Loses_WithExactProblemDetail()
    {
        var contractId = await CreateActiveContractAsync("leaverace");
        _factory.WithDb(db =>
        {
            if (!db.LeavePolicies.Any(p => p.CountryCode == "PH"))
            {
                db.LeavePolicies.Add(new LeavePolicy { CountryCode = "PH", AnnualLeaveDays = 15, SickLeaveDays = 10 });
                db.SaveChanges();
            }
        });
        var leave = await _client.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId,
            type = "Annual",
            startDate = "2026-08-03",
            endDate = "2026-08-04",
        });
        Assert.Equal(HttpStatusCode.Created, leave.StatusCode);
        var leaveId = (await leave.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Id;

        var approve = await _client.PostAsJsonAsync($"/api/leave-requests/{leaveId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var reject = await _client.PostAsJsonAsync($"/api/leave-requests/{leaveId}/reject", new { note = "Too late" });

        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
        var problem = await reject.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Only pending leave requests can be rejected; this request is Approved.", problem!.Detail);
    }

    [Fact]
    public async Task ApproveTermination_AfterContractAlreadyTerminated_LeavesRequestPending()
    {
        var contractId = await CreateActiveContractAsync("termrace");
        var request = await _client.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId,
            reason = "Planned exit",
            proposedEndDate = "2030-12-31",
        });
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        var terminationId = (await request.Content.ReadFromJsonAsync<TerminationRequestResponse>())!.Id;

        // Someone terminates the contract directly (for cause) before the request is decided.
        var direct = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate = "2026-07-31",
            reason = "Gross misconduct",
        });
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);

        var approve = await _client.PostAsJsonAsync($"/api/termination-requests/{terminationId}/approve", new { });

        // The approve fails on the contract transition and the request stays pending.
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var problem = await approve.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Only active contracts can be terminated; this contract is Terminated.", problem!.Detail);

        var reloaded = await _client.GetFromJsonAsync<TerminationRequestResponse>(
            $"/api/termination-requests/{terminationId}");
        Assert.Equal("Pending", reloaded!.Status);

        // The direct termination's end date stands.
        var contract = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contractId}");
        Assert.Equal(new DateOnly(2026, 7, 31), contract!.EndDate);
    }

    [Fact]
    public async Task ApproveAmendment_AfterContractTerminated_LeavesAmendmentPending()
    {
        var contractId = await CreateActiveContractAsync("amendafterterm");
        var amendmentId = await CreatePendingAmendmentAsync(contractId, 140_000m);

        var terminate = await _client.PostAsJsonAsync($"/api/contracts/{contractId}/terminate", new
        {
            endDate = "2026-07-31",
            reason = "Company closed",
        });
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        var approve = await _client.PostAsJsonAsync($"/api/contract-amendments/{amendmentId}/approve", new { });

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var problem = await approve.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Only active contracts can be amended; this contract is Terminated.", problem!.Detail);

        var amendment = await _client.GetFromJsonAsync<AmendmentResponse>($"/api/contract-amendments/{amendmentId}");
        Assert.Equal("Pending", amendment!.Status);

        // No salary record was appended by the failed approval.
        var history = await _client.GetFromJsonAsync<List<SalaryRecordResponse>>(
            $"/api/contracts/{contractId}/salary-history");
        Assert.Single(history!);
        Assert.Equal("Initial", history![0].Source);
    }

    [Fact]
    public async Task StaleWrite_AfterInterleavedRivalWrite_LoserFails_WinnerVisibleViaApi()
    {
        var contractId = await CreateActiveContractAsync("shapecheck");

        using var scopeA = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var tracked = await dbA.Contracts.SingleAsync(c => c.Id == contractId);

        // Interleave: another writer bumps the version between load and save.
        _factory.WithDb(db =>
        {
            var same = db.Contracts.Single(c => c.Id == contractId);
            same.JobTitle = "Renamed by rival";
            db.SaveChanges();
        });

        tracked.JobTitle = "Renamed by loser";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbA.SaveChangesAsync());
        Assert.NotNull(ex);

        var final = await _client.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contractId}");
        Assert.Equal("Renamed by rival", final!.JobTitle);
    }
}
