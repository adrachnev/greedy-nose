namespace GreedyNose.Api.Notifications;

/// <summary>
/// The only forms of an FCM token that may reach a log line or a response body. A token is a delivery
/// credential for one phone, so anything that talks about a token says which one with
/// <see cref="Of"/> and never prints it whole. Shared by everything that handles tokens — registration,
/// sending, the debug endpoint — which is why it does not live with any one of them.
/// </summary>
public static class TokenPreview
{
    /// <summary>
    /// Enough to tell two registrations apart, never the whole token.
    ///
    /// A token shorter than 32 characters is not shown at all: on a short string a 6-character prefix
    /// is a large share of the secret. Real tokens are far longer, so in practice this only affects
    /// test or malformed input.
    /// </summary>
    public static string Of(string token) => token.Length >= 32 ? $"{token[..6]}…" : "…";

    /// <summary>
    /// <paramref name="text"/> with every occurrence of <paramref name="token"/> — verbatim, or
    /// URL-encoded (<c>:</c> as <c>%3A</c>) — replaced by its <see cref="Of"/> form. A backstop for
    /// text we did not write, an error message from Firebase.
    ///
    /// What it does <b>not</b> cover: a token that has been cut, re-encoded some other way or split
    /// across lines is not recognised, and neither is a fragment longer than the preview. It is not
    /// a guarantee, and does not need to be: the token travels in the JSON body of the request, never
    /// in a URL, and FCM's error messages are not known to echo it. Nothing else in this codebase may
    /// lean on it — the rule is still "log <see cref="Of"/>, never the token".
    /// </summary>
    public static string Redact(string text, string token) =>
        token.Length == 0
            ? text
            : text.Replace(token, Of(token), StringComparison.Ordinal)
                  .Replace(Uri.EscapeDataString(token), Of(token), StringComparison.Ordinal);
}
