using System.Diagnostics.CodeAnalysis;

namespace GreedyNose.Api.Notifications;

/// <summary>
/// What <c>POST /device-token</c> accepts, as pure functions — no database, no HTTP — so the contract
/// is tested directly instead of through a request.
///
/// A token is **printable ASCII only** (<c>0x21</c>–<c>0x7E</c>: no whitespace, no control
/// characters, nothing non-ASCII), at most <see cref="MaxLength"/> characters. A real FCM token is
/// ~160 characters of <c>[A-Za-z0-9:_-]</c>, so this rejects nothing genuine. What it does stop are
/// values that would otherwise pass validation and fail later as a 500 inside Postgres: a NUL
/// (<c>text</c> cannot hold it — error 22021) or an over-long value (see <see cref="MaxLength"/>).
///
/// The token is judged and stored **exactly as sent**, never silently trimmed: trimming could only
/// ever fix a client bug, and would store a string FCM never issued — a token that then fails at
/// send time, far from where the mistake was made. A padded or blank token is therefore *rejected*,
/// which is loud and immediate.
/// </summary>
public static class DeviceTokenValidation
{
    /// <summary>
    /// A real FCM token is ~160 characters; 1024 is generous headroom, not a limit anyone should meet.
    ///
    /// The number is not arbitrary. <c>DeviceTokens.Token</c> carries a unique btree index, and
    /// Postgres refuses an index row above ~2704 bytes (error 54000) — a 500 for a value that got
    /// past validation. Because <see cref="TryValidate"/> accepts ASCII only, length equals UTF-8
    /// bytes, so 1024 characters is 1024 bytes: safely under that limit. (With non-ASCII allowed a
    /// character could be up to 4 bytes, and no character count would be safe.) It also stops an
    /// unauthenticated endpoint from being made to store large strings.
    /// </summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// True when <paramref name="token"/> may be stored. Otherwise <paramref name="problem"/> says
    /// why — a fixed sentence that never contains the token, because it goes into the response body.
    /// </summary>
    public static bool TryValidate([NotNullWhen(true)] string? token, [NotNullWhen(false)] out string? problem)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            problem = "A device token is required.";
            return false;
        }

        // Length before content: cheap, and it bounds the scan below.
        if (token.Length > MaxLength)
        {
            problem = $"A device token is at most {MaxLength} characters.";
            return false;
        }

        // '!' to '~' is printable ASCII without the space: it excludes whitespace, every control
        // character (including NUL) and everything non-ASCII in one range.
        if (token.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            problem = "A device token contains only printable ASCII characters, without spaces.";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// The only form of a token that may be logged. An FCM token is a delivery credential for one
    /// phone, so the log gets enough to tell two registrations apart and never the whole thing.
    ///
    /// A token shorter than 32 characters is not shown at all: on a short string a 6-character prefix
    /// is a large share of the secret. Real tokens are far longer, so in practice this only affects
    /// test or malformed input.
    /// </summary>
    public static string Preview(string token) => token.Length >= 32 ? $"{token[..6]}…" : "…";
}
