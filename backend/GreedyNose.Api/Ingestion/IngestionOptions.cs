namespace GreedyNose.Api.Ingestion;

/// <summary>
/// Configuration for the ingestion worker (TRACER-02-NOTIFICATIONS.md, step 6). Same shape as
/// <c>EnableBankingOptions</c>/<c>FirebaseOptions</c> — bound once from config, not user secrets:
/// there is no credential here, just a cadence.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Real default 6h (ASPSP rate limits, see TRACER-01-BANK-DATA.md's Findings). Overridden much
    /// shorter in dev via user-secrets so a tracer-bullet session does not wait hours to see a push.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 21600;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
