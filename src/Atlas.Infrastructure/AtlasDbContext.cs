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
    public DbSet<EmploymentContract> Contracts => Set<EmploymentContract>();
    public DbSet<OnboardingItem> OnboardingItems => Set<OnboardingItem>();
    public DbSet<ComplianceDocument> ComplianceDocuments => Set<ComplianceDocument>();

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

        modelBuilder.Entity<EmploymentContract>(contract =>
        {
            contract.HasKey(c => c.Id);
            contract.Property(c => c.JobTitle).HasMaxLength(200);
            contract.Property(c => c.CurrencyCode).HasMaxLength(3);
            contract.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            contract.Property(c => c.TerminationReason).HasMaxLength(500);
            contract.HasIndex(c => c.WorkerId);
            contract.HasIndex(c => c.ClientId);
            contract.HasIndex(c => new { c.CountryCode, c.Status });
            contract.HasOne(c => c.Client)
                .WithMany()
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            contract.HasOne(c => c.Worker)
                .WithMany()
                .HasForeignKey(c => c.WorkerId)
                .OnDelete(DeleteBehavior.Restrict);
            contract.HasOne(c => c.Country)
                .WithMany()
                .HasForeignKey(c => c.CountryCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OnboardingItem>(item =>
        {
            item.HasKey(i => i.Id);
            item.Property(i => i.Title).HasMaxLength(200);
            item.Property(i => i.Notes).HasMaxLength(1000);
            item.Property(i => i.Type).HasConversion<string>().HasMaxLength(30);
            item.HasIndex(i => new { i.ContractId, i.Type }).IsUnique();
            item.HasOne(i => i.Contract)
                .WithMany()
                .HasForeignKey(i => i.ContractId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComplianceDocument>(doc =>
        {
            doc.HasKey(d => d.Id);
            doc.Property(d => d.Name).HasMaxLength(200);
            doc.Property(d => d.Type).HasConversion<string>().HasMaxLength(30);
            doc.HasIndex(d => d.WorkerId);
            doc.HasIndex(d => d.ExpiryDate);
            doc.HasOne(d => d.Worker)
                .WithMany()
                .HasForeignKey(d => d.WorkerId)
                .OnDelete(DeleteBehavior.Cascade);
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
