using System.Globalization;

namespace GreedyNose.Api.Domain;

/// <summary>
/// Why a debit came out bad, and what the push wording needs to say (R12a). Mirrors the reason union
/// in <c>app/src/domain/classification.ts</c>'s <c>DebitClassification</c> type.
/// </summary>
public enum BadReason
{
    NoRule,
    MarkedBad,
    OverLimit,
}

/// <summary>
/// The result of <see cref="RuleEngine.Classify"/>. TypeScript expresses this as a discriminated
/// union so "over-limit with no limit to quote" is unrepresentable; C# has no equivalent, so the same
/// invariant — <see cref="LimitEUR"/> is set exactly when <see cref="Reason"/> is
/// <see cref="BadReason.OverLimit"/>, and <see cref="Reason"/> is set exactly when
/// <see cref="Classification"/> is <see cref="Classification.Bad"/> — is kept by construction
/// instead: build one only through <see cref="Good"/>/<see cref="NoRule"/>/<see cref="MarkedBad"/>/
/// <see cref="OverLimit"/>, the same factory-method pattern <c>Notifications.SendResult</c> uses.
/// </summary>
public sealed record DebitClassification(Classification Classification, BadReason? Reason, decimal? LimitEUR)
{
    public static DebitClassification Good() => new(Classification.Good, null, null);

    public static DebitClassification NoRule() => new(Classification.Bad, BadReason.NoRule, null);

    public static DebitClassification MarkedBad() => new(Classification.Bad, BadReason.MarkedBad, null);

    public static DebitClassification OverLimit(decimal limitEUR)
    {
        if (limitEUR <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitEUR), limitEUR, "A limit must be positive.");
        }

        return new DebitClassification(Classification.Bad, BadReason.OverLimit, limitEUR);
    }
}

/// <summary>
/// R5's decision table, ported line-for-line from <c>app/src/domain/classification.ts</c>'s
/// <c>classifyDebit</c> + <c>describeBadReason</c> — not redesigned. Depends only on
/// <see cref="Classification"/> (this namespace), never on <c>Data</c>'s EF entities: callers (the
/// step 6 ingestion worker) pass plain values off their own entities, which is what lets this class
/// be unit-tested with no database and no EF Core in scope at all.
/// </summary>
public static class RuleEngine
{
    /// <summary>
    /// R5, in order:
    ///
    /// | Rule for the payee | Result |
    /// |---|---|
    /// | No rule (<paramref name="ruleClassification"/> is <c>null</c>) | bad, <c>no-rule</c> |
    /// | Bad | bad, <c>marked-bad</c> — <paramref name="ruleAmountLimitEUR"/> is *not* consulted |
    /// | Good, no limit | good |
    /// | Good, limit set | good if <paramref name="debitAmountEUR"/> is at most the limit, else bad,
    /// <c>over-limit</c> |
    ///
    /// The second row is the one that is easy to get backwards — the architecture's own `A1` bug:
    /// consulting a leftover limit on a bad payee would silence exactly the payee the user flagged on
    /// purpose, the single failure R1 exists to prevent. A bad payee alerts on every charge, whatever
    /// its size, regardless of any limit still sitting on the rule from when it was good.
    /// </summary>
    /// <param name="debitAmountEUR">The charge's amount. Always positive (R17a).</param>
    /// <param name="ruleClassification">
    /// The payee's current rule classification, or <c>null</c> when no rule exists yet — an unknown
    /// payee is bad until the user says otherwise (R4b, R5).
    /// </param>
    /// <param name="ruleAmountLimitEUR">
    /// The rule's optional limit (R4a). Only consulted while <paramref name="ruleClassification"/> is
    /// <see cref="Classification.Good"/> — a bad rule may still carry an old limit (R8a keeps it so
    /// marking the payee good again restores it), and that limit must not be read here.
    /// </param>
    public static DebitClassification Classify(
        decimal debitAmountEUR, Classification? ruleClassification, decimal? ruleAmountLimitEUR)
    {
        if (ruleClassification is null)
        {
            return DebitClassification.NoRule();
        }

        if (ruleClassification == Classification.Bad)
        {
            return DebitClassification.MarkedBad();
        }

        if (ruleAmountLimitEUR is null)
        {
            return DebitClassification.Good();
        }

        // "Does not exceed" means less than or equal — R5a — so a charge exactly at the limit is
        // good. Strictly >, never >=.
        return debitAmountEUR > ruleAmountLimitEUR.Value
            ? DebitClassification.OverLimit(ruleAmountLimitEUR.Value)
            : DebitClassification.Good();
    }

    /// <summary>
    /// R12a's three bodies, kept next to the reasons that produce them so the two cannot drift apart.
    /// <c>null</c> for a good debit — there is nothing to say.
    /// </summary>
    public static string? DescribeBadReason(DebitClassification result) => result.Reason switch
    {
        null => null,
        BadReason.NoRule => "New payee — you haven't seen this one before.",
        BadReason.MarkedBad => "You marked this payee as bad.",
        BadReason.OverLimit => $"Over your limit of {FormatAmountEUR(result.LimitEUR!.Value)}.",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Reason, "Unknown bad reason."),
    };

    /// <summary>
    /// A fixed de-DE-style amount ("49,00 €") for push text — the server has no device locale to
    /// format with (TRACER-02-NOTIFICATIONS.md's "Formatting default" note). Built explicitly
    /// rather than through <c>ToString("C")</c> or <c>new CultureInfo("de-DE")</c>: both key off
    /// culture data (the current culture, or the OS's installed culture database) which can differ
    /// between machines and containers, where this always produces the same string. In-app screens
    /// the push eventually links to keep using <c>formatCurrencyEUR</c> and the real device locale —
    /// this only affects the transient OS notification text.
    /// </summary>
    public static string FormatAmountEUR(decimal amountEUR) =>
        $"{amountEUR.ToString("F2", CultureInfo.InvariantCulture).Replace('.', ',')} €";
}
