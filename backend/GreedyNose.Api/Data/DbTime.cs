namespace GreedyNose.Api.Data;

/// <summary>
/// Postgres <c>timestamptz</c> keeps whole microseconds; a .NET clock has 100 ns ticks. A value that is written and later
/// compared with what was written (a conditional update, "is the marker still mine?") must therefore be cut to whole
/// microseconds once, before it is written and compared, or the comparison silently fails on the real database.
/// </summary>
public static class DbTime
{
    private const long TicksPerMicrosecond = 10;

    public static DateTimeOffset TruncateToMicroseconds(this DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TicksPerMicrosecond));
}
