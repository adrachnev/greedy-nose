namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// The key that encrypts stored consents (Story 1.7): <c>Consent:EncryptionKey</c>, the base64 of
/// exactly 32 random bytes. Comes from user secrets locally (Key Vault later, Epic 2), never from the
/// committed <c>appsettings.json</c>, and is never logged.
/// </summary>
public sealed class ConsentEncryptionOptions
{
    public const string SectionName = "Consent";

    public const int KeyLengthBytes = 32;

    public string EncryptionKey { get; set; } = "";

    /// <summary>
    /// Precondition for starting the backend, same shape as <c>ApiKeyOptions.Validate</c>: there is no
    /// default key, because a default would be a published one. Returns the key bytes. The messages
    /// name only the setting and the rule, never the value.
    /// </summary>
    public byte[] Validate()
    {
        const string Generate = "Generate one with `openssl rand -base64 32` and set it with " +
                                "`dotnet user-secrets set \"Consent:EncryptionKey\" \"<value>\"`.";

        if (string.IsNullOrWhiteSpace(EncryptionKey))
        {
            throw new InvalidOperationException($"Consent:EncryptionKey is not set. {Generate}");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(EncryptionKey.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Consent:EncryptionKey is not valid base64. {Generate}");
        }

        if (key.Length != KeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"Consent:EncryptionKey must decode to exactly {KeyLengthBytes} bytes. {Generate}");
        }

        return key;
    }
}
