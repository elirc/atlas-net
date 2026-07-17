namespace Atlas.Domain.Entities;

public enum ComplianceDocumentType
{
    Passport = 0,
    Visa = 1,
    WorkPermit = 2,
    ProfessionalCertification = 3,
    Other = 4,
}

public enum ComplianceStatus
{
    Valid = 0,
    ExpiringSoon = 1,
    Expired = 2,
}

/// <summary>A worker's compliance document with expiry tracking.</summary>
public class ComplianceDocument
{
    public const int DefaultExpiringSoonWindowDays = 30;

    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid WorkerId { get; set; }
    public Worker? Worker { get; set; }

    public required ComplianceDocumentType Type { get; set; }

    public required string Name { get; set; }

    public DateOnly? IssuedDate { get; set; }

    /// <summary>Null means the document never expires.</summary>
    public DateOnly? ExpiryDate { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Status as of a given date: Expired when the expiry date has passed,
    /// ExpiringSoon within the warning window, otherwise Valid.
    /// </summary>
    public ComplianceStatus GetStatus(DateOnly asOf, int expiringSoonWindowDays = DefaultExpiringSoonWindowDays)
    {
        if (ExpiryDate is null)
        {
            return ComplianceStatus.Valid;
        }
        if (ExpiryDate.Value < asOf)
        {
            return ComplianceStatus.Expired;
        }
        if (ExpiryDate.Value <= asOf.AddDays(expiringSoonWindowDays))
        {
            return ComplianceStatus.ExpiringSoon;
        }

        return ComplianceStatus.Valid;
    }
}
