using NCrontab;

namespace GreedyNose.Functions;

/// <summary>
/// The floor on the ingestion schedule (Story 2.3). N26 allows about 4 background fetches a day; 6 h is
/// exactly 4. A schedule that fires more often is refused at startup unless
/// <c>Ingestion:AllowShortInterval=true</c> (local testing only). One named constant so it can be
/// lowered if DKB / ING-DiBa turn out to allow more (Epic 3).
/// </summary>
public static class MinPollInterval
{
    public static readonly TimeSpan Floor = TimeSpan.FromHours(6);

    public const string ScheduleSetting = "IngestionSchedule (app setting / environment variable)";
    public const string OptOutSetting = "Ingestion:AllowShortInterval";


    // PRAGMATIC: a pattern that fires only in rare windows (e.g. Feb 29 combined with a weekday) may show
    // fewer than two firings here and so pass unjudged; accepted, the owner sets the schedule.
    // A fixed leap-year start and a four-year window cover every calendar pattern (Feb 29 included).
    private static readonly DateTime WindowStart = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = WindowStart.AddYears(4);

    /// <summary>
    /// Smallest gap between two consecutive firings of <paramref name="ncrontab"/>, or null when the
    /// schedule fires fewer than twice in the window. Stops early once a gap is below
    /// <paramref name="stopBelow"/>, so a per-second schedule stays cheap.
    /// </summary>
    public static TimeSpan? SmallestGap(string ncrontab, TimeSpan? stopBelow = null)
    {
        var schedule = CrontabSchedule.Parse(ncrontab, new CrontabSchedule.ParseOptions { IncludingSeconds = true });
        TimeSpan? smallest = null;
        DateTime? previous = null;
        foreach (var next in schedule.GetNextOccurrences(WindowStart, WindowEnd))
        {
            if (previous is not null)
            {
                var gap = next - previous.Value;
                if (smallest is null || gap < smallest) smallest = gap;
                if (stopBelow is not null && gap < stopBelow) break;
            }
            previous = next;
        }
        return smallest;
    }

    /// <summary>
    /// Throws if the schedule is not valid NCRONTAB or fires more often than <see cref="Floor"/>,
    /// unless <paramref name="allowShortInterval"/>. The message names both settings and never
    /// contains a secret. Leading/trailing whitespace of the schedule is ignored here; the host binds the raw
    /// value, which NCrontab also tolerates.
    /// </summary>
    public static void Enforce(string? schedule, bool allowShortInterval)
    {
        // Fail closed: the host's trigger binding %IngestionSchedule% has no fallback either, so a
        // missing value must stop us here with a clear message instead of an opaque binding error.
        if (string.IsNullOrWhiteSpace(schedule))
        {
            throw new InvalidOperationException(
                $"{ScheduleSetting} is not set. Set it to a six-field NCRONTAB expression, e.g. \"0 0 */6 * * *\".");
        }

        schedule = schedule.Trim();

        TimeSpan? gap;
        try
        {
            gap = SmallestGap(schedule, Floor);
        }
        catch (CrontabException ex)
        {
            throw new InvalidOperationException(
                $"{ScheduleSetting} is not a valid six-field NCRONTAB expression: {ex.Message}");
        }

        if (gap is not null && gap < Floor && !allowShortInterval)
        {
            throw new InvalidOperationException(
                $"{ScheduleSetting} '{schedule}' fires every {gap} at the shortest, below the {Floor} minimum " +
                $"(bank fetch quota). Use a slower schedule, or set {OptOutSetting}=true for local testing only.");
        }
    }
}
