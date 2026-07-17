using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Tests.Infrastructure;

public class DataSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AtlasDbContext _db;

    public DataSeederTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AtlasDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public void Seed_FreshDatabase_PopulatesEveryAggregate()
    {
        DataSeeder.Seed(_db);

        Assert.Equal(6, _db.Countries.Count());
        Assert.Equal(2, _db.Clients.Count());
        Assert.Equal(4, _db.Workers.Count());
        Assert.Equal(3, _db.Contracts.Count());
        Assert.Equal(2, _db.Contracts.Count(c => c.Status == ContractStatus.Active));
        Assert.Equal(1, _db.Contracts.Count(c => c.Status == ContractStatus.Draft));
        Assert.Equal(15, _db.OnboardingItems.Count()); // 3 contracts x 5 items
        Assert.Equal(3, _db.ComplianceDocuments.Count());
    }

    [Fact]
    public void Seed_ActiveContracts_HaveCompletedRequiredOnboarding()
    {
        DataSeeder.Seed(_db);

        var activeContractIds = _db.Contracts
            .Where(c => c.Status == ContractStatus.Active)
            .Select(c => c.Id)
            .ToList();

        var pendingRequired = _db.OnboardingItems
            .Where(i => activeContractIds.Contains(i.ContractId) && i.IsRequired && !i.IsCompleted)
            .Count();

        Assert.Equal(0, pendingRequired);
    }

    [Fact]
    public void Seed_RunTwice_IsIdempotent()
    {
        DataSeeder.Seed(_db);
        DataSeeder.Seed(_db);

        Assert.Equal(6, _db.Countries.Count());
        Assert.Equal(2, _db.Clients.Count());
        Assert.Equal(4, _db.Workers.Count());
    }

    [Fact]
    public void Seed_IncludesExpiringAndExpiredComplianceDocuments()
    {
        DataSeeder.Seed(_db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var documents = _db.ComplianceDocuments.ToList();
        var statuses = documents.Select(d => d.GetStatus(today)).ToList();

        Assert.Contains(ComplianceStatus.Valid, statuses);
        Assert.Contains(ComplianceStatus.ExpiringSoon, statuses);
        Assert.Contains(ComplianceStatus.Expired, statuses);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
