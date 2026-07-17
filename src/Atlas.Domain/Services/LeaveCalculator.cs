namespace Atlas.Domain.Services;

/// <summary>
/// Pure leave math. Leave consumes working days only: Monday through Friday,
/// inclusive of both endpoints. Public holidays are out of scope (simplified).
/// </summary>
public static class LeaveCalculator
{
    /// <summary>Counts the working days (Mon-Fri) in [start, end] inclusive.</summary>
    public static int CountWorkingDays(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new DomainException(
                $"End date {end:yyyy-MM-dd} cannot be before the start date {start:yyyy-MM-dd}.");
        }

        var days = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days++;
            }
        }

        return days;
    }
}
