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
}
