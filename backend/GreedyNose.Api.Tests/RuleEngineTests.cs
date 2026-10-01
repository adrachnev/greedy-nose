using GreedyNose.Api.Domain;

namespace GreedyNose.Api.Tests;

/// <summary>
/// R5's classification table, exhaustively — the C# port of
/// <c>app/src/domain/__tests__/classification.test.ts</c>'s <c>classifyDebit</c>/<c>describeBadReason</c>
/// cases. This is the logic the whole product rests on: get a row wrong and the app either alerts on
/// everything or — far worse — goes quiet about a payee the user flagged.
///
/// The architecture calls out two ways to get it backwards (see ARCHITECTURE.md's "Rule engine"), and
/// both have a test here: the amount belonging to the *good* branch only (the `A1` bug), and equality
/// counting as good (R5a).
/// </summary>
public class RuleEngineTests
{
    private const decimal LimitEUR = 30m;

    [Fact]
    public void No_rule_is_bad_reason_no_rule_R4b_R5()
    {
        var result = RuleEngine.Classify(debitAmountEUR: 9.99m, ruleClassification: null, ruleAmountLimitEUR: null);

        Assert.Equal(Classification.Bad, result.Classification);
        Assert.Equal(BadReason.NoRule, result.Reason);
        Assert.Null(result.LimitEUR);
    }

    /// <summary>
    /// The `A1` bug, as a test: the architecture had the rule engine evaluating the amount for *bad*
    /// payees. Built that way, a payee marked bad while carrying an old limit falls silent for every
    /// charge under it — the exact failure R1 exists to prevent. A bad rule alerts on every charge,
    /// whatever its size, and a leftover limit from when the payee was good must not be consulted.
    /// </summary>
    [Fact]
    public void Bad_rule_is_bad_reason_marked_bad_and_ignores_a_leftover_limit_A1()
    {
        var wellUnderTheLeftoverLimit = RuleEngine.Classify(
            debitAmountEUR: 5m, ruleClassification: Classification.Bad, ruleAmountLimitEUR: 100m);

        Assert.Equal(Classification.Bad, wellUnderTheLeftoverLimit.Classification);
        Assert.Equal(BadReason.MarkedBad, wellUnderTheLeftoverLimit.Reason);
        Assert.Null(wellUnderTheLeftoverLimit.LimitEUR);
    }

    [Theory]
    [InlineData(9.99)]
    [InlineData(9_999_999)]
    public void Good_rule_with_no_limit_is_good_whatever_the_size_R4a(decimal amountEUR)
    {
        var result = RuleEngine.Classify(amountEUR, ruleClassification: Classification.Good, ruleAmountLimitEUR: null);

        Assert.Equal(Classification.Good, result.Classification);
        Assert.Null(result.Reason);
        Assert.Null(result.LimitEUR);
    }

    [Fact]
    public void Good_rule_charge_under_the_limit_is_good()
    {
        var result = RuleEngine.Classify(18.47m, Classification.Good, LimitEUR);

        Assert.Equal(Classification.Good, result.Classification);
        Assert.Null(result.Reason);
    }

    /// <summary>R5a: "does not exceed" is &lt;=, so the boundary itself is good, not bad.</summary>
    [Fact]
    public void Good_rule_charge_exactly_at_the_limit_is_good_R5a()
    {
        var result = RuleEngine.Classify(LimitEUR, Classification.Good, LimitEUR);

        Assert.Equal(Classification.Good, result.Classification);
        Assert.Null(result.Reason);
    }

    /// <summary>One cent over is the first bad amount.</summary>
    [Fact]
    public void Good_rule_charge_one_cent_over_the_limit_is_bad_R5a()
    {
        var result = RuleEngine.Classify(LimitEUR + 0.01m, Classification.Good, LimitEUR);

        Assert.Equal(Classification.Bad, result.Classification);
        Assert.Equal(BadReason.OverLimit, result.Reason);
        Assert.Equal(LimitEUR, result.LimitEUR);
    }

    [Fact]
    public void Good_rule_charge_over_the_limit_is_bad_and_carries_the_limit_for_the_wording()
    {
        var result = RuleEngine.Classify(34.21m, Classification.Good, LimitEUR);

        Assert.Equal(Classification.Bad, result.Classification);
        Assert.Equal(BadReason.OverLimit, result.Reason);
        Assert.Equal(LimitEUR, result.LimitEUR);
    }

    // --- describeBadReason / R12a wording -----------------------------------------------------

    [Fact]
    public void A_good_debit_has_no_reason_to_give()
    {
        Assert.Null(RuleEngine.DescribeBadReason(DebitClassification.Good()));
    }

    [Fact]
    public void No_rule_words_as_new_payee_R12a()
    {
        var result = RuleEngine.Classify(9.99m, null, null);

        Assert.Equal("New payee — you haven't seen this one before.", RuleEngine.DescribeBadReason(result));
    }

    [Fact]
    public void Marked_bad_words_as_you_marked_this_payee_R12a()
    {
        var result = RuleEngine.Classify(9.99m, Classification.Bad, null);

        Assert.Equal("You marked this payee as bad.", RuleEngine.DescribeBadReason(result));
    }

    /// <summary>
    /// The server has no device locale (unlike the app's `describeBadReason`, which the TS test
    /// leaves locale-agnostic on purpose): the wording is pinned to the exact fixed de-DE-style
    /// format TRACER-02-NOTIFICATIONS.md's "Formatting default" decided.
    /// </summary>
    [Fact]
    public void Over_limit_words_the_amount_in_the_fixed_de_DE_style_format_R12a()
    {
        var result = RuleEngine.Classify(34.21m, Classification.Good, LimitEUR);

        Assert.Equal("Over your limit of 30,00 €.", RuleEngine.DescribeBadReason(result));
    }

    [Fact]
    public void Formats_a_limit_with_a_fractional_cent_correctly()
    {
        var result = RuleEngine.Classify(50m, Classification.Good, 12.5m);

        Assert.Equal("Over your limit of 12,50 €.", RuleEngine.DescribeBadReason(result));
    }

    /// <summary>
    /// R17a, ported from classification.test.ts's equivalent case. Already unreachable by
    /// construction here — <see cref="DebitClassification.OverLimit"/> throws on a non-positive
    /// limit, and <see cref="RuleEngine.FormatAmountEUR"/> never negates — but pinned explicitly
    /// anyway, so the invariant stays visible (and tested) even if either guard is ever loosened.
    /// </summary>
    [Fact]
    public void Over_limit_wording_never_carries_a_minus_sign_R17a()
    {
        var result = RuleEngine.Classify(34.21m, Classification.Good, LimitEUR);
        var text = RuleEngine.DescribeBadReason(result)!;

        Assert.DoesNotContain("−", text);
        Assert.DoesNotContain("-", text);
    }
}
