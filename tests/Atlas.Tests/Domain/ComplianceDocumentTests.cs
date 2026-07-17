using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

public class ComplianceDocumentTests
{
    private static readonly DateOnly Today = new(2026, 7, 16);

    private static ComplianceDocument NewDocument(DateOnly? expiry) => new()
    {
        WorkerId = Guid.NewGuid(),
        Type = ComplianceDocumentType.Visa,
        Name = "Test visa",
        ExpiryDate = expiry,
    };

    [Fact]
    public void GetStatus_NoExpiryDate_IsAlwaysValid()
    {
        var doc = NewDocument(expiry: null);

        Assert.Equal(ComplianceStatus.Valid, doc.GetStatus(Today));
    }

    [Fact]
    public void GetStatus_ExpiryFarInFuture_IsValid()
    {
        var doc = NewDocument(Today.AddDays(31));

        Assert.Equal(ComplianceStatus.Valid, doc.GetStatus(Today));
    }

    [Fact]
    public void GetStatus_ExpiryExactlyAtWindowEdge_IsExpiringSoon()
    {
        var doc = NewDocument(Today.AddDays(30));

        Assert.Equal(ComplianceStatus.ExpiringSoon, doc.GetStatus(Today));
    }

    [Fact]
    public void GetStatus_ExpiryToday_IsExpiringSoonNotExpired()
    {
        var doc = NewDocument(Today);

        Assert.Equal(ComplianceStatus.ExpiringSoon, doc.GetStatus(Today));
    }

    [Fact]
    public void GetStatus_ExpiryYesterday_IsExpired()
    {
        var doc = NewDocument(Today.AddDays(-1));

        Assert.Equal(ComplianceStatus.Expired, doc.GetStatus(Today));
    }

    [Fact]
    public void GetStatus_CustomWindow_ChangesExpiringSoonBoundary()
    {
        var doc = NewDocument(Today.AddDays(45));

        Assert.Equal(ComplianceStatus.Valid, doc.GetStatus(Today, expiringSoonWindowDays: 30));
        Assert.Equal(ComplianceStatus.ExpiringSoon, doc.GetStatus(Today, expiringSoonWindowDays: 60));
    }
}

public class OnboardingItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDefaultChecklist_HasFourRequiredItemsAndOneOptional()
    {
        var contractId = Guid.NewGuid();

        var checklist = OnboardingItem.CreateDefaultChecklist(contractId);

        Assert.Equal(5, checklist.Count);
        Assert.Equal(4, checklist.Count(i => i.IsRequired));
        Assert.All(checklist, i => Assert.Equal(contractId, i.ContractId));
        Assert.All(checklist, i => Assert.False(i.IsCompleted));
    }

    [Fact]
    public void Complete_SetsCompletionStateAndNotes()
    {
        var item = OnboardingItem.CreateDefaultChecklist(Guid.NewGuid())[0];

        item.Complete(Now, "  Checked against passport  ");

        Assert.True(item.IsCompleted);
        Assert.Equal(Now, item.CompletedAtUtc);
        Assert.Equal("Checked against passport", item.Notes);
    }

    [Fact]
    public void Complete_Twice_Throws()
    {
        var item = OnboardingItem.CreateDefaultChecklist(Guid.NewGuid())[0];
        item.Complete(Now);

        Assert.Throws<DomainException>(() => item.Complete(Now));
    }
}
