namespace Atlas.Domain.Entities;

public enum OnboardingItemType
{
    IdentityDocument = 0,
    RightToWorkCheck = 1,
    BankDetails = 2,
    SignedContract = 3,
    TaxForms = 4,
}

/// <summary>
/// A single item on a contract's onboarding checklist. A contract cannot be
/// activated until every required item is completed.
/// </summary>
public class OnboardingItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ContractId { get; set; }
    public EmploymentContract? Contract { get; set; }

    public required OnboardingItemType Type { get; set; }

    public required string Title { get; set; }

    public bool IsRequired { get; set; } = true;

    public bool IsCompleted { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public string? Notes { get; set; }

    public void Complete(DateTimeOffset nowUtc, string? notes = null)
    {
        if (IsCompleted)
        {
            throw new DomainException($"Onboarding item '{Title}' is already completed.");
        }

        IsCompleted = true;
        CompletedAtUtc = nowUtc;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            Notes = notes.Trim();
        }
    }

    /// <summary>The standard checklist created for every new employment contract.</summary>
    public static List<OnboardingItem> CreateDefaultChecklist(Guid contractId) =>
    [
        new() { ContractId = contractId, Type = OnboardingItemType.IdentityDocument, Title = "Government-issued identity document", IsRequired = true },
        new() { ContractId = contractId, Type = OnboardingItemType.RightToWorkCheck, Title = "Right-to-work verification", IsRequired = true },
        new() { ContractId = contractId, Type = OnboardingItemType.BankDetails, Title = "Bank account details for payroll", IsRequired = true },
        new() { ContractId = contractId, Type = OnboardingItemType.SignedContract, Title = "Signed employment contract", IsRequired = true },
        new() { ContractId = contractId, Type = OnboardingItemType.TaxForms, Title = "Local tax forms", IsRequired = false },
    ];
}
