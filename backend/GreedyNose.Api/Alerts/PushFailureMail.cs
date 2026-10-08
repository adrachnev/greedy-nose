using System.Globalization;
using System.Text;

namespace GreedyNose.Api.Alerts;

/// <summary>What the failure mail may say: counts, ages, times, outcome names and bank names. Nothing else exists to leak.</summary>
public sealed record PushFailureFacts(
    string Reasons,
    int ConsecutiveFailures,
    DateTimeOffset FirstFailureAt,
    DateTimeOffset LastFailureAt,
    int WaitingBadDebits,
    TimeSpan? OldestWaitingAge,
    IReadOnlyList<string> Banks);

/// <summary>
/// Story 2.10's mail texts as pure functions (pattern of <c>ReconnectSummary.Build</c>): no clock, no
/// database, so the "no payee, amount, id, token, key or account number" rule is testable on the output.
/// <c>Reasons</c> is a comma list of outcome names written by the ingestion runner.
/// </summary>
public static class PushFailureMail
{
    public static AlertMail BuildFailure(PushFailureFacts facts)
    {
        var text = new StringBuilder();
        text.AppendLine($"Push alerts are failing: {facts.ConsecutiveFailures} attempts in a row did not reach your phone.");
        text.AppendLine();
        text.AppendLine($"Reason: {facts.Reasons}");
        text.AppendLine($"Failing since: {Format(facts.FirstFailureAt)}");
        text.AppendLine($"Last try: {Format(facts.LastFailureAt)}");
        text.AppendLine($"Bad debits waiting: {facts.WaitingBadDebits}"
                        + (facts.OldestWaitingAge is { } age ? $" (oldest waiting {FormatAge(age)})" : ""));
        text.AppendLine("Retries continue on every poll until a push is 24 hours old; after that it is given up.");
        text.AppendLine($"Affected bank(s): {(facts.Banks.Count == 0 ? "unknown" : string.Join(", ", facts.Banks))}");
        text.AppendLine();
        text.AppendLine("Check first:");
        foreach (var line in CheckFirstLines(facts.Reasons))
        {
            text.AppendLine($"- {line}");
        }

        text.AppendLine();
        text.AppendLine("You get one mail per incident, and one short follow-up when pushes work again.");

        return new AlertMail($"Greedy Nose: push alerts are failing ({facts.Reasons})", text.ToString());
    }

    public static AlertMail BuildRecovery(int sentDebits, DateTimeOffset failingSince, DateTimeOffset recoveredAt)
    {
        var text = new StringBuilder();
        text.AppendLine("Push alerts work again.");
        text.AppendLine();
        text.AppendLine($"Failing since: {Format(failingSince)}");
        text.AppendLine($"Working again: {Format(recoveredAt)}");
        text.AppendLine($"Waiting bad debits sent since then: {sentDebits}");
        return new AlertMail("Greedy Nose: push alerts work again", text.ToString());
    }

    /// <summary>One line per distinct reason, in a fixed order; an unknown name gets the generic line.</summary>
    public static IReadOnlyList<string> CheckFirstLines(string reasons)
    {
        var names = reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();

        if (names.Remove("Rejected"))
        {
            lines.Add("Rejected: check the Firebase Admin key in Key Vault and that it belongs to the right Firebase project; check the function logs.");
        }

        if (names.Remove("Transient"))
        {
            lines.Add("Transient: likely a temporary FCM or network problem; check Google's status page.");
        }

        var noDevice = names.Remove("NoToken");
        noDevice |= names.Remove("TokenNoLongerValid");
        if (noDevice)
        {
            lines.Add("No device token or token no longer valid: open the app once so it registers again.");
        }

        if (names.Count > 0 || lines.Count == 0)
        {
            lines.Add("Other reason: check the function logs.");
        }

        return lines;
    }

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1
            ? $"{(int)age.TotalHours} h {age.Minutes} min"
            : $"{Math.Max(0, (int)age.TotalMinutes)} min";
}
