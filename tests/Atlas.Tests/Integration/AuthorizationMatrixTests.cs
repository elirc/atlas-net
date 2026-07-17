using System.Net;
using System.Net.Http.Json;
using Atlas.Api.Endpoints;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Integration;

/// <summary>
/// Role x endpoint x ownership matrix. The policy under test: platform-only
/// endpoints 403 for any client-scoped role; writes on your own client's data
/// 403 for viewers; anything belonging to another client 404s (existence is
/// never revealed cross-client, even on write attempts).
/// </summary>
public class AuthorizationMatrixTests : IClassFixture<AtlasApiFactory>
{
    private const string ClientAdminKey = "matrix-client-admin-key";
    private const string ClientViewerKey = "matrix-client-viewer-key";

    private readonly AtlasApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly Guid _ownClientId;

    public AuthorizationMatrixTests(AtlasApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClient();

        Guid ownClientId = default;
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

            var existing = db.Clients.SingleOrDefault(c => c.Name == "Matrix Own Co");
            if (existing is null)
            {
                var client = new Client
                {
                    Name = "Matrix Own Co",
                    LegalName = "Matrix Own Co",
                    BillingEmail = "matrix.own@example.com",
                    HeadquartersCountryCode = "PH",
                    BillingCurrencyCode = "PHP",
                };
                db.Clients.Add(client);
                db.ApiUsers.Add(new ApiUser
                {
                    Name = "Matrix client admin",
                    ApiKey = ClientAdminKey,
                    Role = ApiRole.ClientAdmin,
                    ClientId = client.Id,
                });
                db.ApiUsers.Add(new ApiUser
                {
                    Name = "Matrix client viewer",
                    ApiKey = ClientViewerKey,
                    Role = ApiRole.ClientViewer,
                    ClientId = client.Id,
                });
                ownClientId = client.Id;
            }
            else
            {
                ownClientId = existing.Id;
            }
            db.SaveChanges();
        });
        _ownClientId = ownClientId;
    }

    /// <summary>Creates a worker + contract for the client, completes onboarding, activates it.</summary>
    private async Task<ContractResponse> CreateActiveContractAsync(Guid clientId, string prefix)
    {
        var workerResponse = await _admin.PostAsJsonAsync("/api/workers", new
        {
            fullName = $"{prefix} Worker",
            email = $"{prefix}.{Guid.NewGuid():N}@example.com",
            countryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();

        var contractResponse = await _admin.PostAsJsonAsync("/api/contracts", new
        {
            clientId,
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
        var activate = await _admin.PostAsync($"/api/contracts/{contract!.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        return contract;
    }

    private async Task<Guid> CreateOtherClientAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/clients", new
        {
            name,
            billingEmail = $"{name.ToLowerInvariant().Replace(' ', '.')}.{Guid.NewGuid():N}@example.com",
            headquartersCountryCode = "PH",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClientResponse>())!.Id;
    }

    public static TheoryData<string, string, string> PlatformOnlyEndpoints()
    {
        var data = new TheoryData<string, string, string>();
        (string Method, string Path)[] endpoints =
        [
            ("GET", "/api/payroll-runs"),
            ("GET", $"/api/payroll-runs/{Guid.Empty}"),
            ("POST", "/api/payroll-runs"),
            ("POST", $"/api/payroll-runs/{Guid.Empty}/complete"),
            ("GET", "/api/reports/headcount"),
            ("GET", "/api/reports/payroll-costs"),
            ("GET", "/api/reports/compliance-expiries"),
            ("GET", "/api/reports/invoice-aging"),
            ("GET", "/api/api-users"),
            ("POST", "/api/api-users"),
            ("POST", $"/api/api-users/{Guid.Empty}/deactivate"),
            ("POST", "/api/countries"),
            ("POST", "/api/fx-rates"),
            ("POST", "/api/leave-policies"),
            ("POST", "/api/benefit-plans"),
            ("POST", $"/api/benefit-plans/{Guid.Empty}/deactivate"),
            ("POST", "/api/workers"),
            ("POST", "/api/clients"),
            ("GET", "/api/compliance/expiring"),
            ("POST", $"/api/workers/{Guid.Empty}/documents"),
        ];
        foreach (var (method, path) in endpoints)
        {
            data.Add(method, path, "ClientAdmin");
            data.Add(method, path, "ClientViewer");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PlatformOnlyEndpoints))]
    public async Task PlatformOnlyEndpoint_AsClientScopedRole_Returns403(string method, string path, string role)
    {
        var key = role == "ClientAdmin" ? ClientAdminKey : ClientViewerKey;
        var caller = _factory.CreateClientWithApiKey(key);

        var response = method == "GET"
            ? await caller.GetAsync(path)
            : await caller.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryWriteAction_OnOwnClientsData_AsViewer_Returns403()
    {
        var contract = await CreateActiveContractAsync(_ownClientId, "viewer403");

        var leave = await _admin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Annual",
            startDate = "2026-08-03",
            endDate = "2026-08-04",
        });
        Assert.Equal(HttpStatusCode.Created, leave.StatusCode);
        var leaveId = (await leave.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Id;

        var expense = await _admin.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = contract.Id,
            items = new[] { new { description = "Taxi", amount = 25.00, incurredDate = "2026-07-01" } },
        });
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
        var expenseId = (await expense.Content.ReadFromJsonAsync<ExpenseClaimResponse>())!.Id;

        var amendment = await _admin.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = contract.Id,
            newMonthlySalary = 110_000,
            effectiveDate = "2026-09-01",
        });
        Assert.Equal(HttpStatusCode.Created, amendment.StatusCode);
        var amendmentId = (await amendment.Content.ReadFromJsonAsync<AmendmentResponse>())!.Id;

        var termination = await _admin.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = contract.Id,
            reason = "Role eliminated",
            proposedEndDate = "2030-12-31",
        });
        Assert.Equal(HttpStatusCode.Created, termination.StatusCode);
        var terminationId = (await termination.Content.ReadFromJsonAsync<TerminationRequestResponse>())!.Id;

        var checklist = await _admin.GetFromJsonAsync<OnboardingChecklistResponse>(
            $"/api/contracts/{contract.Id}/onboarding");
        var optionalItem = checklist!.Items.First(i => !i.IsCompleted);

        var viewer = _factory.CreateClientWithApiKey(ClientViewerKey);
        (string Path, object Body)[] writes =
        [
            ("/api/contracts", new { clientId = _ownClientId, workerId = contract.WorkerId, jobTitle = "X", monthlySalary = 1, startDate = "2026-01-01" }),
            ($"/api/contracts/{contract.Id}/activate", new { }),
            ($"/api/contracts/{contract.Id}/terminate", new { endDate = "2026-12-31", reason = "No" }),
            ("/api/leave-requests", new { contractId = contract.Id, type = "Annual", startDate = "2026-10-05", endDate = "2026-10-06" }),
            ($"/api/leave-requests/{leaveId}/approve", new { }),
            ($"/api/leave-requests/{leaveId}/reject", new { note = "no" }),
            ($"/api/leave-requests/{leaveId}/cancel", new { }),
            ("/api/expense-claims", new { contractId = contract.Id, items = new[] { new { description = "Bus", amount = 5.00, incurredDate = "2026-07-01" } } }),
            ($"/api/expense-claims/{expenseId}/approve", new { }),
            ($"/api/expense-claims/{expenseId}/reject", new { note = "no" }),
            ("/api/contract-amendments", new { contractId = contract.Id, newMonthlySalary = 120_000, effectiveDate = "2026-10-01" }),
            ($"/api/contract-amendments/{amendmentId}/approve", new { }),
            ($"/api/contract-amendments/{amendmentId}/reject", new { note = "no" }),
            ($"/api/contract-amendments/{amendmentId}/cancel", new { }),
            ("/api/termination-requests", new { contractId = contract.Id, reason = "Downsizing", proposedEndDate = "2030-12-31" }),
            ($"/api/termination-requests/{terminationId}/approve", new { }),
            ($"/api/termination-requests/{terminationId}/reject", new { note = "no" }),
            ($"/api/termination-requests/{terminationId}/cancel", new { }),
            ($"/api/contracts/{contract.Id}/onboarding/{optionalItem.Id}/complete", new { }),
        ];

        foreach (var (path, body) in writes)
        {
            var response = await viewer.PostAsJsonAsync(path, body);

            Assert.True(HttpStatusCode.Forbidden == response.StatusCode,
                $"POST {path} as viewer returned {(int)response.StatusCode}, expected 403.");
        }

        // The viewer's write attempts must not have changed anything.
        var leaveAfter = await _admin.GetFromJsonAsync<LeaveRequestResponse>($"/api/leave-requests/{leaveId}");
        Assert.Equal("Pending", leaveAfter!.Status);
        var contractAfter = await _admin.GetFromJsonAsync<ContractResponse>($"/api/contracts/{contract.Id}");
        Assert.Equal("Active", contractAfter!.Status);
    }

    [Fact]
    public async Task EveryRead_OfAnotherClientsData_Returns404_NotForbidden()
    {
        var otherClientId = await CreateOtherClientAsync("Matrix Hidden Co");
        var otherContract = await CreateActiveContractAsync(otherClientId, "hidden404");

        var leave = await _admin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = otherContract.Id,
            type = "Annual",
            startDate = "2026-08-10",
            endDate = "2026-08-11",
        });
        var leaveId = (await leave.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Id;

        var expense = await _admin.PostAsJsonAsync("/api/expense-claims", new
        {
            contractId = otherContract.Id,
            items = new[] { new { description = "Hotel", amount = 300.00, incurredDate = "2026-07-01" } },
        });
        var expenseId = (await expense.Content.ReadFromJsonAsync<ExpenseClaimResponse>())!.Id;

        var amendment = await _admin.PostAsJsonAsync("/api/contract-amendments", new
        {
            contractId = otherContract.Id,
            newMonthlySalary = 111_000,
            effectiveDate = "2026-09-01",
        });
        var amendmentId = (await amendment.Content.ReadFromJsonAsync<AmendmentResponse>())!.Id;

        var termination = await _admin.PostAsJsonAsync("/api/termination-requests", new
        {
            contractId = otherContract.Id,
            reason = "Role eliminated",
            proposedEndDate = "2030-12-31",
        });
        var terminationId = (await termination.Content.ReadFromJsonAsync<TerminationRequestResponse>())!.Id;

        // Both scoped roles of "Matrix Own Co" must see 404 on every read.
        foreach (var key in new[] { ClientAdminKey, ClientViewerKey })
        {
            var caller = _factory.CreateClientWithApiKey(key);
            string[] reads =
            [
                $"/api/clients/{otherClientId}",
                $"/api/contracts/{otherContract.Id}",
                $"/api/workers/{otherContract.WorkerId}",
                $"/api/leave-requests/{leaveId}",
                $"/api/expense-claims/{expenseId}",
                $"/api/contract-amendments/{amendmentId}",
                $"/api/termination-requests/{terminationId}",
                $"/api/contracts/{otherContract.Id}/onboarding",
                $"/api/contracts/{otherContract.Id}/salary-history",
                $"/api/contracts/{otherContract.Id}/leave-balances",
            ];
            foreach (var path in reads)
            {
                var response = await caller.GetAsync(path);

                Assert.True(HttpStatusCode.NotFound == response.StatusCode,
                    $"GET {path} with {key} returned {(int)response.StatusCode}, expected 404.");
            }
        }
    }

    [Fact]
    public async Task WriteAttempts_OnAnotherClientsData_Return404_EvenForClientAdmins()
    {
        var otherClientId = await CreateOtherClientAsync("Matrix Sealed Co");
        var otherContract = await CreateActiveContractAsync(otherClientId, "sealed404");
        var leave = await _admin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = otherContract.Id,
            type = "Annual",
            startDate = "2026-09-07",
            endDate = "2026-09-08",
        });
        var leaveId = (await leave.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Id;

        var clientAdmin = _factory.CreateClientWithApiKey(ClientAdminKey);
        (string Path, object Body)[] writes =
        [
            ($"/api/contracts/{otherContract.Id}/activate", new { }),
            ($"/api/contracts/{otherContract.Id}/terminate", new { endDate = "2026-12-31", reason = "No" }),
            ($"/api/leave-requests/{leaveId}/approve", new { }),
            ($"/api/leave-requests/{leaveId}/cancel", new { }),
        ];

        foreach (var (path, body) in writes)
        {
            var response = await clientAdmin.PostAsJsonAsync(path, body);

            Assert.True(HttpStatusCode.NotFound == response.StatusCode,
                $"POST {path} as another client's admin returned {(int)response.StatusCode}, expected 404.");
        }

        // Submitting against a foreign contract id also 404-shapes as validation,
        // never confirming the contract exists.
        var createLeave = await clientAdmin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = otherContract.Id,
            type = "Annual",
            startDate = "2026-10-05",
            endDate = "2026-10-06",
        });
        Assert.Equal(HttpStatusCode.BadRequest, createLeave.StatusCode);
        var body404 = await createLeave.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body404);
    }

    [Fact]
    public async Task ClientAdmin_CanDecideOwnClientsRequests()
    {
        var contract = await CreateActiveContractAsync(_ownClientId, "adminok");
        var leave = await _admin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = contract.Id,
            type = "Sick",
            startDate = "2026-11-02",
            endDate = "2026-11-03",
        });
        Assert.Equal(HttpStatusCode.Created, leave.StatusCode);
        var leaveId = (await leave.Content.ReadFromJsonAsync<LeaveRequestResponse>())!.Id;

        var clientAdmin = _factory.CreateClientWithApiKey(ClientAdminKey);
        var approve = await clientAdmin.PostAsJsonAsync($"/api/leave-requests/{leaveId}/approve", new { });

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = await approve.Content.ReadFromJsonAsync<LeaveRequestResponse>();
        Assert.Equal("Approved", approved!.Status);
    }

    [Theory]
    [InlineData("/api/contracts")]
    [InlineData("/api/leave-requests")]
    [InlineData("/api/expense-claims")]
    [InlineData("/api/contract-amendments")]
    [InlineData("/api/termination-requests")]
    [InlineData("/api/benefit-enrollments")]
    [InlineData("/api/invoices")]
    public async Task ListEndpoints_AsViewer_NeverLeakOtherClientsRows(string path)
    {
        var otherClientId = await CreateOtherClientAsync($"Matrix Leak Co {path.GetHashCode():x}");
        var otherContract = await CreateActiveContractAsync(otherClientId, $"leak{Math.Abs(path.GetHashCode())}");
        await _admin.PostAsJsonAsync("/api/leave-requests", new
        {
            contractId = otherContract.Id,
            type = "Annual",
            startDate = "2026-12-07",
            endDate = "2026-12-08",
        });

        var viewer = _factory.CreateClientWithApiKey(ClientViewerKey);
        // Filtering by the foreign contract explicitly must still return nothing.
        var response = await viewer.GetAsync($"{path}?pageSize=200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(otherContract.Id.ToString(), raw);
        Assert.DoesNotContain(otherClientId.ToString(), raw);
    }
}
