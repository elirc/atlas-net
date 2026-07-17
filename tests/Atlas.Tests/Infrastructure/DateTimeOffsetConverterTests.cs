using Atlas.Domain.Entities;
using Atlas.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Tests.Infrastructure;

/// <summary>
/// SQLite cannot order or compare DateTimeOffset natively; the DbContext converts it
/// to UTC ticks (long). These tests prove ordering and comparison work in SQL.
/// </summary>
public class DateTimeOffsetConverterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AtlasDbContext _db;

    public DateTimeOffsetConverterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AtlasDbContext(options);
        _db.Database.EnsureCreated();

        _db.Countries.Add(new Country
        {
            Code = "US",
            Name = "United States",
            CurrencyCode = "USD",
            EmployerCostRate = 0.10m,
            EmployeeDeductionRate = 0.20m,
        });
        _db.SaveChanges();
    }

    private Client NewClient(string name, DateTimeOffset createdAt) => new()
    {
        Name = name,
        LegalName = name,
        BillingEmail = $"{name.ToLowerInvariant()}@example.com",
        HeadquartersCountryCode = "US",
        CreatedAtUtc = createdAt,
    };

    [Fact]
    public void OrderBy_DateTimeOffset_SortsChronologicallyInSql()
    {
        _db.Clients.AddRange(
            NewClient("middle", new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)),
            NewClient("newest", new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)),
            NewClient("oldest", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));
        _db.SaveChanges();

        var names = _db.Clients.OrderBy(c => c.CreatedAtUtc).Select(c => c.Name).ToList();

        Assert.Equal(["oldest", "middle", "newest"], names);
    }

    [Fact]
    public void Where_DateTimeOffsetComparison_FiltersInSql()
    {
        _db.Clients.AddRange(
            NewClient("early", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            NewClient("late", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)));
        _db.SaveChanges();

        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var late = _db.Clients.Where(c => c.CreatedAtUtc > cutoff).Select(c => c.Name).ToList();

        Assert.Equal(["late"], late);
    }

    [Fact]
    public void NonUtcOffset_RoundTrips_AsSameInstant()
    {
        var manila = new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.FromHours(8));
        _db.Clients.Add(NewClient("manila", manila));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var reloaded = _db.Clients.Single(c => c.Name == "manila");

        // Offset identity is not preserved (stored as UTC ticks) but the instant is.
        Assert.Equal(manila.UtcDateTime, reloaded.CreatedAtUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, reloaded.CreatedAtUtc.Offset);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
