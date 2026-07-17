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

        db.Workers.AddRange(
            new Worker { FullName = "Maria Santos", Email = "maria.santos@example.com", CountryCode = "PH", DateOfBirth = new DateOnly(1993, 4, 12) },
            new Worker { FullName = "Jonas Weber", Email = "jonas.weber@example.com", CountryCode = "DE", DateOfBirth = new DateOnly(1988, 11, 3) },
            new Worker { FullName = "Priya Nair", Email = "priya.nair@example.com", CountryCode = "IN", DateOfBirth = new DateOnly(1996, 7, 25) },
            new Worker { FullName = "Lucas Oliveira", Email = "lucas.oliveira@example.com", CountryCode = "BR", DateOfBirth = new DateOnly(1990, 1, 30) });

        db.SaveChanges();
    }
}
