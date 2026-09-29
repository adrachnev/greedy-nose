namespace GreedyNose.Api.Ingestion;

/// <summary>
/// The one push for a dead consent (R19). Verbatim from R19, like <see cref="ReconnectSummary"/>: an
/// ordinary push with fixed wording.
/// </summary>
public static class ConnectionExpiredNotice
{
    public const string Title = "Bank connection expired";
    public const string Body = "Reconnect to keep getting alerts.";
}
