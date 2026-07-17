namespace Atlas.Domain.Services;

/// <summary>
/// Pure final-pay math for a contract's last month of employment:
/// calendar-day proration of the final month's salary plus a payout of
/// unused annual leave at a working-day rate (260 working days a year).
/// All amounts round to 2 dp away from zero like the rest of payroll.
/// </summary>
public static class FinalPayCalculator
{
    /// <summary>Working days per year used to derive the daily rate.</summary>
    public const int WorkingDaysPerYear = 260;

    /// <summary>
    /// Prorates the final month's salary by calendar days: employment ends on
    /// <paramref name="endDate"/> (inclusive), so a June 15 end pays 15/30ths.
    /// An end on the month's last day pays the full salary.
    /// </summary>
    public static decimal ProrateFinalMonth(decimal monthlySalary, DateOnly endDate)
    {
        if (monthlySalary <= 0)
        {
            throw new DomainException("Monthly salary must be greater than zero.");
        }

        var daysInMonth = DateTime.DaysInMonth(endDate.Year, endDate.Month);
        return PayrollCalculator.RoundMoney(monthlySalary * endDate.Day / daysInMonth);
    }

    /// <summary>Daily rate: 12 monthly salaries spread over 260 working days.</summary>
    public static decimal DailyRate(decimal monthlySalary) =>
        PayrollCalculator.RoundMoney(monthlySalary * 12 / WorkingDaysPerYear);

    /// <summary>Pays out unused annual leave days at the daily rate.</summary>
    public static decimal UnusedLeavePayout(decimal monthlySalary, int unusedLeaveDays)
    {
        if (unusedLeaveDays < 0)
        {
            throw new DomainException("Unused leave days cannot be negative.");
        }

        return PayrollCalculator.RoundMoney(DailyRate(monthlySalary) * unusedLeaveDays);
    }
}
