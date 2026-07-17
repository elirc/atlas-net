using Atlas.Domain;
using Atlas.Domain.Entities;
using Atlas.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure;

/// <summary>One leave type's balance for a contract in one calendar year.</summary>
public record LeaveBalance(
    LeaveType Type,
    int AllowanceDays,
    int ApprovedDays,
    int PendingDays,
    int RemainingDays);

/// <summary>
/// Orchestrates leave: submissions are validated against the contract state,
/// the country's leave policy, existing (pending/approved) requests, and the
/// remaining balance for the calendar year of the leave.
/// </summary>
public class LeaveService
{
    private readonly AtlasDbContext _db;

    public LeaveService(AtlasDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Creates a pending leave request. Leave must fall within one calendar year,
    /// start on or after the contract start date, cover at least one working day,
    /// not overlap another pending/approved request, and fit the remaining balance.
    /// </summary>
    public async Task<LeaveRequest> CreateRequestAsync(
        EmploymentContract contract, LeaveType type, DateOnly startDate, DateOnly endDate, string? reason)
    {
        if (contract.Status != ContractStatus.Active)
        {
            throw new DomainException($"Leave can only be requested on active contracts; this contract is {contract.Status}.");
        }
        if (startDate.Year != endDate.Year)
        {
            throw new DomainException("A leave request must fall within a single calendar year.");
        }
        if (startDate < contract.StartDate)
        {
            throw new DomainException($"Leave cannot start before the contract start date {contract.StartDate:yyyy-MM-dd}.");
        }
        if (contract.EndDate is not null && endDate > contract.EndDate.Value)
        {
            throw new DomainException($"Leave cannot end after the contract end date {contract.EndDate.Value:yyyy-MM-dd}.");
        }

        var days = LeaveCalculator.CountWorkingDays(startDate, endDate); // throws when end < start
        if (days == 0)
        {
            throw new DomainException("The requested period contains no working days.");
        }

        var policy = await GetPolicyAsync(contract.CountryCode);

        var existing = await _db.LeaveRequests
            .Where(r => r.ContractId == contract.Id
                        && (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved))
            .ToListAsync();

        var overlap = existing.FirstOrDefault(r => r.Overlaps(startDate, endDate));
        if (overlap is not null)
        {
            throw new DomainException(
                $"The requested period overlaps an existing {overlap.Status.ToString().ToLowerInvariant()} " +
                $"leave request ({overlap.StartDate:yyyy-MM-dd} to {overlap.EndDate:yyyy-MM-dd}).");
        }

        var reserved = existing
            .Where(r => r.Type == type && r.StartDate.Year == startDate.Year)
            .Sum(r => r.Days);
        var remaining = policy.AllowanceFor(type) - reserved;
        if (days > remaining)
        {
            throw new DomainException(
                $"Insufficient {type.ToString().ToLowerInvariant()} leave balance for {startDate.Year}: " +
                $"requested {days} day(s) but only {remaining} remain(s) of {policy.AllowanceFor(type)}.");
        }

        var request = new LeaveRequest
        {
            ContractId = contract.Id,
            Type = type,
            StartDate = startDate,
            EndDate = endDate,
            Days = days,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };
        _db.LeaveRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    /// <summary>Computes per-type balances for a contract in one calendar year.</summary>
    public async Task<List<LeaveBalance>> GetBalancesAsync(EmploymentContract contract, int year)
    {
        var policy = await GetPolicyAsync(contract.CountryCode);

        var requests = await _db.LeaveRequests
            .Where(r => r.ContractId == contract.Id
                        && (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved))
            .ToListAsync();

        return Enum.GetValues<LeaveType>()
            .Select(type =>
            {
                var inYear = requests.Where(r => r.Type == type && r.StartDate.Year == year).ToList();
                var approved = inYear.Where(r => r.Status == LeaveRequestStatus.Approved).Sum(r => r.Days);
                var pending = inYear.Where(r => r.Status == LeaveRequestStatus.Pending).Sum(r => r.Days);
                var allowance = policy.AllowanceFor(type);
                return new LeaveBalance(type, allowance, approved, pending, allowance - approved - pending);
            })
            .ToList();
    }

    private async Task<LeavePolicy> GetPolicyAsync(string countryCode) =>
        await _db.LeavePolicies.SingleOrDefaultAsync(p => p.CountryCode == countryCode)
        ?? throw new DomainException($"No leave policy is configured for country '{countryCode}'.");
}
