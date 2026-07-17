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
        };
        var nordwind = new Client
        {
            Name = "Nordwind Analytics",
            LegalName = "Nordwind Analytics GmbH",
            BillingEmail = "accounts@nordwind.example",
            HeadquartersCountryCode = "DE",
            ManagementFeeRate = 0.08m,
        };
        db.Clients.AddRange(acme, nordwind);

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
