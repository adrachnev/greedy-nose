namespace GreedyNose.Api.Auth;

/// <summary>
/// The shared secret the app sends as <c>Authorization: Bearer &lt;key&gt;</c> (Story 1.3). Comes from
/// user secrets / app settings of the host (<c>Api:Key</c>), never from the committed
/// <c>appsettings.json</c>, and is never logged.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Api";

    /// <summary>Shortest key we accept. A short key is a guessable key; fail closed instead.</summary>
    public const int MinimumKeyLength = 32;

    public string Key { get; set; } = "";

    /// <summary>
    /// Precondition for starting the backend: a key is set and long enough. Throws without echoing the
    /// key (the message names only the setting and the length rule).
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new InvalidOperationException(
                "Api:Key is not set. Configure it with `dotnet user-secrets set \"Api:Key\" \"<random, at least " +
                $"{MinimumKeyLength} characters>\"` — every endpoint except /connect and /callback requires it.");
        }

        // Kestrel trims header values, so a key with edge whitespace could never match.
        if (Key != Key.Trim())
        {
            throw new InvalidOperationException(
                "Api:Key has leading or trailing whitespace. Remove it; header values are trimmed, so it could never match.");
        }

        if (Key.Length < MinimumKeyLength)
        {
            throw new InvalidOperationException(
                $"Api:Key is shorter than {MinimumKeyLength} characters. Use a long random value.");
        }
    }
}
