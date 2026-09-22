namespace GreedyNose.Api.Domain;

/// <summary>
/// Mirrors <c>Classification</c> in <c>app/src/domain/model.ts</c>: a payee is good or bad, nothing
/// in between. It lives in <c>Domain</c>, not <c>Data</c>, because the rule engine (step 5) and the
/// persistence layer both need it and neither should have to depend on the other — the same split
/// as the app's <c>src/domain/</c>.
/// </summary>
public enum Classification
{
    Good,
    Bad,
}

/// <summary>
/// The text form of <see cref="Classification"/>: exactly the strings the app uses (<c>good</c>,
/// <c>bad</c>). It is the one place they are spelled out — the database column, its check
/// constraint and, from step 4, the JSON on <c>POST /rules</c> all read from here, so they cannot
/// drift apart.
///
/// The enum's member names (<c>Good</c>) are deliberately not the wire format: that would make the
/// stored value disagree with the client's vocabulary for no benefit.
///
/// An unknown value throws rather than defaulting. Reading a corrupt value as good or bad would be
/// a wrong alert decision, so failing loudly is the safer direction.
/// </summary>
public static class ClassificationText
{
    public const string Good = "good";

    public const string Bad = "bad";

    public static string ToText(this Classification value) => value switch
    {
        Classification.Good => Good,
        Classification.Bad => Bad,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown classification."),
    };

    public static Classification Parse(string text) => text switch
    {
        Good => Classification.Good,
        Bad => Classification.Bad,
        _ => throw new FormatException($"'{text}' is not a classification (expected '{Good}' or '{Bad}')."),
    };

    /// <summary>
    /// Same mapping as <see cref="Parse"/>, without the throw: for a client-supplied string (e.g.
    /// <c>POST /rules</c>'s body), where an unrecognised value is a bad request (400), not a corrupt
    /// stored value. <see cref="Parse"/> stays reserved for reading the database, where "unrecognised"
    /// really does mean something is broken and failing loudly is correct.
    /// </summary>
    public static bool TryParse(string? text, out Classification classification)
    {
        switch (text)
        {
            case Good:
                classification = Classification.Good;
                return true;
            case Bad:
                classification = Classification.Bad;
                return true;
            default:
                classification = default;
                return false;
        }
    }
}
