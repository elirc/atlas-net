namespace Atlas.Domain.Entities;

/// <summary>A client company that hires workers through Atlas.</summary>
public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public required string LegalName { get; set; }

    public required string BillingEmail { get; set; }

    /// <summary>Country of the client's headquarters (ISO 3166-1 alpha-2).</summary>
    public required string HeadquartersCountryCode { get; set; }

    /// <summary>
    /// Atlas management fee as a fraction of gross payroll billed on each
    /// invoice, e.g. 0.10 for 10%.
    /// </summary>
    public decimal ManagementFeeRate { get; set; } = 0.10m;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
