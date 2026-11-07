using System.Globalization;

namespace Hoard.Core.Scheduling;

/// <summary>
/// "Is it time?" for jobs the desktop app runs while it is open. Each schedule names an occurrence (a local date and
/// time); a job is due once the clock has passed the latest occurrence and that occurrence has not been run yet. So a
/// job missed while the app was closed runs once when it next starts, and never twice for the same occurrence.
/// </summary>
public static class Schedules
{
    /// <summary>Parses "HH:mm" (or "H:mm"); anything else gives <paramref name="fallback"/>.</summary>
    public static TimeOnly ParseTime(string? text, TimeOnly fallback) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

    public static string FormatTime(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Today's occurrence if the time has passed, else yesterday's.</summary>
    public static DateTime LatestDaily(DateTime nowLocal, TimeOnly at)
    {
        var today = nowLocal.Date + at.ToTimeSpan();
        return nowLocal >= today ? today : today.AddDays(-1);
    }

    /// <summary>The most recent <paramref name="day"/> at <paramref name="at"/> that is not in the future.</summary>
    public static DateTime LatestWeekly(DateTime nowLocal, DayOfWeek day, TimeOnly at)
    {
        var back = ((int)nowLocal.DayOfWeek - (int)day + 7) % 7;
        var candidate = nowLocal.Date.AddDays(-back) + at.ToTimeSpan();
        return candidate <= nowLocal ? candidate : candidate.AddDays(-7);
    }

    public static DateTime NextWeekly(DateTime nowLocal, DayOfWeek day, TimeOnly at) => LatestWeekly(nowLocal, day, at).AddDays(7);

    public static DateTime NextDaily(DateTime nowLocal, TimeOnly at) => LatestDaily(nowLocal, at).AddDays(1);

    /// <summary>
    /// Due when the latest occurrence has not run yet. Turning a schedule on records the latest occurrence as run, so it
    /// starts with the next one instead of firing at once for one that already passed.
    /// </summary>
    public static bool IsDue(DateTime latestOccurrence, DateTime? lastRunFor) => lastRunFor is not { } last || last < latestOccurrence;

    /// <summary>The seven days a weekly report sent at <paramref name="occurrence"/> covers: [start, end) ending the day before.</summary>
    public static (DateOnly Start, DateOnly EndExclusive) WeekBefore(DateTime occurrence)
    {
        var end = DateOnly.FromDateTime(occurrence.Date);
        return (end.AddDays(-7), end);
    }
}
