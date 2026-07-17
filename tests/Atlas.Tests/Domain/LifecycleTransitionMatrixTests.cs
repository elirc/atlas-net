using Atlas.Domain;
using Atlas.Domain.Entities;

namespace Atlas.Tests.Domain;

/// <summary>
/// Exhaustive invalid-transition matrix for every state machine: from each
/// non-pending state, every transition must throw a DomainException with the
/// exact message the API surfaces as a 409 ProblemDetails detail.
/// </summary>
public class LifecycleTransitionMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static LeaveRequest Leave(LeaveRequestStatus status) => new()
    {
        ContractId = Guid.NewGuid(),
        Type = LeaveType.Annual,
        StartDate = new DateOnly(2026, 8, 3),
        EndDate = new DateOnly(2026, 8, 7),
        Days = 5,
        Status = status,
    };

    [Theory]
    [InlineData(LeaveRequestStatus.Approved)]
    [InlineData(LeaveRequestStatus.Rejected)]
    [InlineData(LeaveRequestStatus.Cancelled)]
    public void LeaveRequest_NonPending_RejectsEveryTransition(LeaveRequestStatus status)
    {
        var approve = Assert.Throws<DomainException>(() => Leave(status).Approve(Now));
        Assert.Equal($"Only pending leave requests can be approved; this request is {status}.", approve.Message);

        var reject = Assert.Throws<DomainException>(() => Leave(status).Reject("note", Now));
        Assert.Equal($"Only pending leave requests can be rejected; this request is {status}.", reject.Message);

        var cancel = Assert.Throws<DomainException>(() => Leave(status).Cancel(Now));
        Assert.Equal($"Only pending leave requests can be cancelled; this request is {status}.", cancel.Message);
    }

    private static ExpenseClaim Claim(ExpenseClaimStatus status) => new()
    {
        ContractId = Guid.NewGuid(),
        CurrencyCode = "PHP",
        Status = status,
    };

    [Theory]
    [InlineData(ExpenseClaimStatus.Approved)]
    [InlineData(ExpenseClaimStatus.Rejected)]
    [InlineData(ExpenseClaimStatus.Reimbursed)]
    public void ExpenseClaim_NonPending_RejectsApproveAndReject(ExpenseClaimStatus status)
    {
        var approve = Assert.Throws<DomainException>(() => Claim(status).Approve(Now));
        Assert.Equal($"Only pending expense claims can be approved; this claim is {status}.", approve.Message);

        var reject = Assert.Throws<DomainException>(() => Claim(status).Reject("note", Now));
        Assert.Equal($"Only pending expense claims can be rejected; this claim is {status}.", reject.Message);
    }

    [Theory]
    [InlineData(ExpenseClaimStatus.Pending)]
    [InlineData(ExpenseClaimStatus.Rejected)]
    [InlineData(ExpenseClaimStatus.Reimbursed)]
    public void ExpenseClaim_NonApproved_CannotBeReimbursed(ExpenseClaimStatus status)
    {
        var ex = Assert.Throws<DomainException>(() => Claim(status).MarkReimbursed(Guid.NewGuid(), Now));
        Assert.Equal($"Only approved expense claims can be reimbursed; this claim is {status}.", ex.Message);
    }

    private static ContractAmendment Amendment(AmendmentStatus status) => new()
    {
        ContractId = Guid.NewGuid(),
        NewMonthlySalary = 2_000m,
        EffectiveDate = new DateOnly(2026, 8, 1),
        Status = status,
    };

    [Theory]
    [InlineData(AmendmentStatus.Approved)]
    [InlineData(AmendmentStatus.Rejected)]
    [InlineData(AmendmentStatus.Cancelled)]
    public void Amendment_NonPending_RejectsEveryTransition(AmendmentStatus status)
    {
        var approve = Assert.Throws<DomainException>(() => Amendment(status).Approve(Now));
        Assert.Equal($"Only pending amendments can be approved; this amendment is {status}.", approve.Message);

        var reject = Assert.Throws<DomainException>(() => Amendment(status).Reject("note", Now));
        Assert.Equal($"Only pending amendments can be rejected; this amendment is {status}.", reject.Message);

        var cancel = Assert.Throws<DomainException>(() => Amendment(status).Cancel(Now));
        Assert.Equal($"Only pending amendments can be cancelled; this amendment is {status}.", cancel.Message);
    }

    private static TerminationRequest Termination(TerminationRequestStatus status) => new()
    {
        ContractId = Guid.NewGuid(),
        Reason = "Restructuring",
        NoticeDate = new DateOnly(2026, 7, 1),
        ProposedEndDate = new DateOnly(2026, 8, 15),
        Status = status,
    };

    [Theory]
    [InlineData(TerminationRequestStatus.Approved)]
    [InlineData(TerminationRequestStatus.Rejected)]
    [InlineData(TerminationRequestStatus.Cancelled)]
    public void TerminationRequest_NonPending_RejectsEveryTransition(TerminationRequestStatus status)
    {
        var approve = Assert.Throws<DomainException>(() => Termination(status).Approve(Now));
        Assert.Equal($"Only pending termination requests can be approved; this request is {status}.", approve.Message);

        var reject = Assert.Throws<DomainException>(() => Termination(status).Reject("note", Now));
        Assert.Equal($"Only pending termination requests can be rejected; this request is {status}.", reject.Message);

        var cancel = Assert.Throws<DomainException>(() => Termination(status).Cancel(Now));
        Assert.Equal($"Only pending termination requests can be cancelled; this request is {status}.", cancel.Message);
    }

    private static EmploymentContract Contract(ContractStatus status) => new()
    {
        ClientId = Guid.NewGuid(),
        WorkerId = Guid.NewGuid(),
        CountryCode = "PH",
        JobTitle = "Engineer",
        MonthlySalary = 1_000m,
        CurrencyCode = "PHP",
        StartDate = new DateOnly(2026, 1, 1),
        Status = status,
    };

    [Theory]
    [InlineData(ContractStatus.Active)]
    [InlineData(ContractStatus.Terminated)]
    public void Contract_NonDraft_CannotBeActivated(ContractStatus status)
    {
        var ex = Assert.Throws<DomainException>(() => Contract(status).Activate(Now));
        Assert.Equal($"Only draft contracts can be activated; this contract is {status}.", ex.Message);
    }

    [Theory]
    [InlineData(ContractStatus.Draft)]
    [InlineData(ContractStatus.Terminated)]
    public void Contract_NonActive_CannotBeTerminated(ContractStatus status)
    {
        var ex = Assert.Throws<DomainException>(
            () => Contract(status).Terminate(new DateOnly(2026, 6, 30), "Done", Now));
        Assert.Equal($"Only active contracts can be terminated; this contract is {status}.", ex.Message);
    }

    [Theory]
    [InlineData(ContractStatus.Draft)]
    [InlineData(ContractStatus.Terminated)]
    public void Contract_NonActive_CannotBeAmended(ContractStatus status)
    {
        var ex = Assert.Throws<DomainException>(() => Contract(status).ApplyAmendment(2_000m, null));
        Assert.Equal($"Only active contracts can be amended; this contract is {status}.", ex.Message);
    }

    [Fact]
    public void Enrollment_Ended_CannotBeEndedAgain()
    {
        var enrollment = new BenefitEnrollment
        {
            ContractId = Guid.NewGuid(),
            BenefitPlanId = Guid.NewGuid(),
            StartDate = new DateOnly(2026, 1, 1),
            Status = BenefitEnrollmentStatus.Ended,
        };

        var ex = Assert.Throws<DomainException>(() => enrollment.End(new DateOnly(2026, 6, 30), Now));
        Assert.Equal("Only active enrollments can be ended; this enrollment is Ended.", ex.Message);
    }

    [Fact]
    public void PayrollRun_Completed_CannotBeCompletedAgain()
    {
        var run = new PayrollRun { CountryCode = "PH", Year = 2026, Month = 7 };
        run.Complete(Now);

        var ex = Assert.Throws<DomainException>(() => run.Complete(Now));
        Assert.Equal("Only draft payroll runs can be completed; this run is Completed.", ex.Message);
    }

    [Fact]
    public void SuccessfulDecisions_TrimNotes_AndBlankNotesBecomeNull()
    {
        var approvedWithNote = Leave(LeaveRequestStatus.Pending);
        approvedWithNote.Approve(Now, "  looks fine  ");
        Assert.Equal("looks fine", approvedWithNote.DecisionNote);

        var approvedBlankNote = Leave(LeaveRequestStatus.Pending);
        approvedBlankNote.Approve(Now, "   ");
        Assert.Null(approvedBlankNote.DecisionNote);

        var rejected = Leave(LeaveRequestStatus.Pending);
        rejected.Reject("  too busy  ", Now);
        Assert.Equal("too busy", rejected.DecisionNote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejections_AcrossAllAggregates_RequireANote(string? note)
    {
        Assert.Throws<DomainException>(() => Leave(LeaveRequestStatus.Pending).Reject(note!, Now));
        Assert.Throws<DomainException>(() => Claim(ExpenseClaimStatus.Pending).Reject(note!, Now));
        Assert.Throws<DomainException>(() => Amendment(AmendmentStatus.Pending).Reject(note!, Now));
        Assert.Throws<DomainException>(() => Termination(TerminationRequestStatus.Pending).Reject(note!, Now));
    }
}
