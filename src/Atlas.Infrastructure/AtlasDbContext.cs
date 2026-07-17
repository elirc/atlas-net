using Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Atlas.Infrastructure;

public class AtlasDbContext : DbContext
{
    public AtlasDbContext(DbContextOptions<AtlasDbContext> options) : base(options)
    {
    }

    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Worker> Workers => Set<Worker>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order/compare DateTimeOffset natively; store as UTC ticks (long).
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Country>(country =>
        {
            country.HasKey(c => c.Code);
            country.Property(c => c.Code).HasMaxLength(2);
            country.Property(c => c.Name).HasMaxLength(100);
            country.Property(c => c.CurrencyCode).HasMaxLength(3);
        });

        modelBuilder.Entity<Client>(client =>
        {
            client.HasKey(c => c.Id);
            client.Property(c => c.Name).HasMaxLength(200);
            client.Property(c => c.LegalName).HasMaxLength(200);
            client.Property(c => c.BillingEmail).HasMaxLength(320);
            client.HasIndex(c => c.Name);
            client.HasOne<Country>()
                .WithMany()
                .HasForeignKey(c => c.HeadquartersCountryCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Worker>(worker =>
        {
            worker.HasKey(w => w.Id);
            worker.Property(w => w.FullName).HasMaxLength(200);
            worker.Property(w => w.Email).HasMaxLength(320);
            worker.HasIndex(w => w.Email).IsUnique();
            worker.HasOne(w => w.Country)
                .WithMany()
                .HasForeignKey(w => w.CountryCode)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>Stores DateTimeOffset as UTC ticks so SQLite can order and compare it.</summary>
    private sealed class DateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToTicksConverter()
            : base(
                value => value.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
        {
        }
    }
}
