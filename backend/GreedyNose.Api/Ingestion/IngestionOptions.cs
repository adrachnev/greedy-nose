namespace GreedyNose.Api.Ingestion;

/// <summary>
/// Configuration for the ingestion tick. Bound from config — there is no credential here. The
/// cadence itself is not an option of this class: the timer Function's schedule comes from the app
/// setting <c>IngestionSchedule</c> (NCRONTAB, Story 2.3) and is read by the Functions host.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Opt-out of the 6 h floor on the schedule (<c>Ingestion:AllowShortInterval</c>). Only for local
    /// testing against the Sandbox — a short interval against a real bank burns its daily fetch quota.
    /// Read by the Functions host's startup check.
    /// </summary>
    public bool AllowShortInterval { get; set; }

    /// <summary>Default for <see cref="BankTimeout"/>: well below the timer's 120 s tick limit, so a hanging bank cannot eat the whole tick.</summary>
    public static readonly TimeSpan DefaultBankTimeout = TimeSpan.FromSeconds(40);

    /// <summary>Time one bank's turn may take within a tick (<c>Ingestion:BankTimeout</c>, e.g. <c>00:00:40</c>). A timeout is recorded as that bank's error and the walk goes on.</summary>
    public TimeSpan BankTimeout { get; set; } = DefaultBankTimeout;
}
