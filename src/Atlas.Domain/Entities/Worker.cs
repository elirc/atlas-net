namespace Atlas.Domain.Entities;

/// <summary>An individual employed by Atlas on behalf of a client.</summary>
public class Worker
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string FullName { get; set; }

    /// <summary>Unique contact email.</summary>
    public required string Email { get; set; }

    /// <summary>Country of residence / employment (ISO 3166-1 alpha-2).</summary>
    public required string CountryCode { get; set; }

    public Country? Country { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
