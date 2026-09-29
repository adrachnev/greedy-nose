namespace GreedyNose.Api.Ingestion;

/// <summary>
/// The one push after a reconnect gap (R20, mock 05b). Pure text: <c>PushMessage</c> has no second
/// shape, the summary is an ordinary push with different wording. Verbatim from R20, deliberately
/// not pluralised — the "1 new debits" wording is the owner's call for REQUIREMENTS.md, not decided here.
/// </summary>
public static class ReconnectSummary
{
    /// <param name="newDebits">N: every new debit of the gap, good and bad.</param>
    /// <param name="badDebits">M: how many of them classify as bad.</param>
    public static (string Title, string Body) Build(int newDebits, int badDebits) =>
        ($"{newDebits} new debits while you were disconnected", $"{badDebits} of them are bad. Tap to review them.");
}
