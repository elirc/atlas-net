using Atlas.Domain.Entities;

namespace Atlas.Infrastructure;

/// <summary>Seeds a fresh database with sample data for local development.</summary>
public static class DataSeeder
{
    public static void Seed(AtlasDbContext db)
    {
        if (db.Countries.Any())
        {
            return; // Already seeded.
        }

        var countries = new List<Country>
        {
            new() { Code = "US", Name = "United States", CurrencyCode = "USD", EmployerCostRate = 0.10m, EmployeeDeductionRate = 0.22m },
            new() { Code = "GB", Name = "United Kingdom", CurrencyCode = "GBP", EmployerCostRate = 0.138m, EmployeeDeductionRate = 0.25m },
            new() { Code = "DE", Name = "Germany", CurrencyCode = "EUR", EmployerCostRate = 0.21m, EmployeeDeductionRate = 0.30m },
            new() { Code = "PH", Name = "Philippines", CurrencyCode = "PHP", EmployerCostRate = 0.12m, EmployeeDeductionRate = 0.15m },
            new() { Code = "BR", Name = "Brazil", CurrencyCode = "BRL", EmployerCostRate = 0.28m, EmployeeDeductionRate = 0.18m },
            new() { Code = "IN", Name = "India", CurrencyCode = "INR", EmployerCostRate = 0.13m, EmployeeDeductionRate = 0.12m },
        };
        db.Countries.AddRange(countries);

        var acme = new Client
        {
            Name = "Acme Robotics",
            LegalName = "Acme Robotics, Inc.",
            BillingEmail = "billing@acmerobotics.example",
            HeadquartersCountryCode = "US",
            ManagementFeeRate = 0.10m,
            BillingCurrencyCode = "USD",
        };
        var nordwind = new Client
        {
            Name = "Nordwind Analytics",
            LegalName = "Nordwind Analytics GmbH",
            BillingEmail = "accounts@nordwind.example",
            HeadquartersCountryCode = "DE",
            ManagementFeeRate = 0.08m,
            BillingCurrencyCode = "EUR",
        };
        db.Clients.AddRange(acme, nordwind);

        db.FxRates.AddRange(
            new FxRate { BaseCurrencyCode = "PHP", QuoteCurrencyCode = "USD", Rate = 0.0171m, EffectiveDate = new DateOnly(2026, 1, 1) },
            new FxRate { BaseCurrencyCode = "PHP", QuoteCurrencyCode = "USD", Rate = 0.0175m, EffectiveDate = new DateOnly(2026, 7, 1) },
            new FxRate { BaseCurrencyCode = "INR", QuoteCurrencyCode = "USD", Rate = 0.0116m, EffectiveDate = new DateOnly(2026, 1, 1) },
            new FxRate { BaseCurrencyCode = "BRL", QuoteCurrencyCode = "USD", Rate = 0.1840m, EffectiveDate = new DateOnly(2026, 1, 1) },
            new FxRate { BaseCurrencyCode = "GBP", QuoteCurrencyCode = "USD", Rate = 1.3100m, EffectiveDate = new DateOnly(2026, 1, 1) },
            new FxRate { BaseCurrencyCode = "USD", QuoteCurrencyCode = "EUR", Rate = 0.9200m, EffectiveDate = new DateOnly(2026, 1, 1) });

        var maria = new Worker { FullName = "Maria Santos", Email = "maria.santos@example.com", CountryCode = "PH", DateOfBirth = new DateOnly(1993, 4, 12) };
        var jonas = new Worker { FullName = "Jonas Weber", Email = "jonas.weber@example.com", CountryCode = "DE", DateOfBirth = new DateOnly(1988, 11, 3) };
        var priya = new Worker { FullName = "Priya Nair", Email = "priya.nair@example.com", CountryCode = "IN", DateOfBirth = new DateOnly(1996, 7, 25) };
        var lucas = new Worker { FullName = "Lucas Oliveira", Email = "lucas.oliveira@example.com", CountryCode = "BR", DateOfBirth = new DateOnly(1990, 1, 30) };
        db.Workers.AddRange(maria, jonas, priya, lucas);

        var mariaContract = new EmploymentContract
        {
            ClientId = acme.Id,
            WorkerId = maria.Id,
            CountryCode = "PH",
            JobTitle = "Senior Software Engineer",
            MonthlySalary = 180_000m,
            CurrencyCode = "PHP",
            StartDate = new DateOnly(2026, 1, 1),
        };
        mariaContract.Activate(DateTimeOffset.UtcNow);

        var jonasContract = new EmploymentContract
        {
            ClientId = nordwind.Id,
            WorkerId = jonas.Id,
            CountryCode = "DE",
            JobTitle = "Data Analyst",
            MonthlySalary = 5_500m,
            CurrencyCode = "EUR",
            StartDate = new DateOnly(2026, 3, 1),
        };
        jonasContract.Activate(DateTimeOffset.UtcNow);

        var priyaContract = new EmploymentContract
        {
            ClientId = acme.Id,
            WorkerId = priya.Id,
            CountryCode = "IN",
            JobTitle = "QA Engineer",
            MonthlySalary = 150_000m,
            CurrencyCode = "INR",
            StartDate = new DateOnly(2026, 8, 1),
        };

        db.Contracts.AddRange(mariaContract, jonasContract, priyaContract);

        foreach (var contract in new[] { mariaContract, jonasContract, priyaContract })
        {
            db.SalaryRecords.Add(new SalaryRecord
            {
                ContractId = contract.Id,
                MonthlySalary = contract.MonthlySalary,
                JobTitle = contract.JobTitle,
                EffectiveDate = contract.StartDate,
                Source = SalaryRecordSource.Initial,
            });
            var checklist = OnboardingItem.CreateDefaultChecklist(contract.Id);
            if (contract.Status == ContractStatus.Active)
            {
                foreach (var item in checklist.Where(i => i.IsRequired))
                {
                    item.Complete(DateTimeOffset.UtcNow, "Verified during seeding");
                }
            }
            db.OnboardingItems.AddRange(checklist);
        }

        db.LeavePolicies.AddRange(
            new LeavePolicy { CountryCode = "US", AnnualLeaveDays = 15, SickLeaveDays = 10 },
            new LeavePolicy { CountryCode = "GB", AnnualLeaveDays = 28, SickLeaveDays = 10 },
            new LeavePolicy { CountryCode = "DE", AnnualLeaveDays = 30, SickLeaveDays = 20 },
            new LeavePolicy { CountryCode = "PH", AnnualLeaveDays = 15, SickLeaveDays = 15 },
            new LeavePolicy { CountryCode = "BR", AnnualLeaveDays = 30, SickLeaveDays = 15 },
            new LeavePolicy { CountryCode = "IN", AnnualLeaveDays = 18, SickLeaveDays = 12 });

        db.LeaveRequests.Add(new LeaveRequest
        {
            ContractId = mariaContract.Id,
            Type = LeaveType.Annual,
            StartDate = new DateOnly(2026, 8, 3),
            EndDate = new DateOnly(2026, 8, 7),
            Days = 5,
            Reason = "Family holiday",
        });

        db.ApiUsers.AddRange(
            new ApiUser { Name = "Atlas Ops (dev)", ApiKey = "dev-admin-key", Role = ApiRole.PlatformAdmin },
            new ApiUser { Name = "Acme Robotics admin (dev)", ApiKey = "dev-acme-admin-key", Role = ApiRole.ClientAdmin, ClientId = acme.Id },
            new ApiUser { Name = "Acme Robotics viewer (dev)", ApiKey = "dev-acme-viewer-key", Role = ApiRole.ClientViewer, ClientId = acme.Id });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.ComplianceDocuments.AddRange(
            new ComplianceDocument
            {
                WorkerId = maria.Id,
                Type = ComplianceDocumentType.Passport,
                Name = "Philippine passport",
                IssuedDate = today.AddYears(-4),
                ExpiryDate = today.AddYears(6),
            },
            new ComplianceDocument
            {
                WorkerId = jonas.Id,
                Type = ComplianceDocumentType.WorkPermit,
                Name = "EU work permit",
                IssuedDate = today.AddYears(-1),
                ExpiryDate = today.AddDays(21), // shows up in the expiring-soon report
            },
            new ComplianceDocument
            {
                WorkerId = priya.Id,
                Type = ComplianceDocumentType.ProfessionalCertification,
                Name = "ISTQB certification",
                IssuedDate = today.AddYears(-3),
                ExpiryDate = today.AddDays(-10), // already expired
            });

        db.SaveChanges();
    }
}
