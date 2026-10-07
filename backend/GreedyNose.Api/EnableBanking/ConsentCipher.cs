using System.Security.Cryptography;

namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// A stored consent could not be decrypted — wrong key, a damaged value, or a value moved to another
/// row. Thrown at startup restore so the backend stops loudly instead of running with a silently
/// empty consent (which would mean alerts that quietly never come). The message never carries
/// plaintext, ciphertext or the key.
/// </summary>
public sealed class ConsentDecryptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// AES-256-GCM for stored consents (Story 1.7, NFR7). A value is stored as
/// <c>[version byte 1][12-byte random nonce][16-byte tag][ciphertext]</c>. The associated data
/// binds the value to its row and column: a ciphertext copied to another row (or the session-id
/// column swapped with the payload column) fails authentication. Nothing here logs.
/// </summary>
public sealed class ConsentCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public ConsentCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != ConsentEncryptionOptions.KeyLengthBytes)
        {
            throw new ArgumentException($"The key must be exactly {ConsentEncryptionOptions.KeyLengthBytes} bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    public byte[] Encrypt(byte[] plaintext, string associatedData)
    {
        var result = new byte[1 + NonceSize + TagSize + plaintext.Length];
        result[0] = Version;
        var nonce = result.AsSpan(1, NonceSize);
        var tag = result.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = result.AsSpan(1 + NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, System.Text.Encoding.UTF8.GetBytes(associatedData));
        return result;
    }

    /// <exception cref="ConsentDecryptionException">Wrong key, tampered value, wrong row or unknown format.</exception>
    public byte[] Decrypt(byte[] stored, string associatedData)
    {
        if (stored.Length < 1 + NonceSize + TagSize || stored[0] != Version)
        {
            throw new ConsentDecryptionException("A stored consent value has an unknown format.");
        }

        var plaintext = new byte[stored.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(
                stored.AsSpan(1, NonceSize),
                stored.AsSpan(1 + NonceSize + TagSize),
                stored.AsSpan(1 + NonceSize, TagSize),
                plaintext,
                System.Text.Encoding.UTF8.GetBytes(associatedData));
        }
        catch (CryptographicException exception)
        {
            // Not echoing the inner message: it is generic, and nothing about the value belongs in a log.
            throw new ConsentDecryptionException(
                "A stored consent could not be decrypted. Check Consent:EncryptionKey — it must be the key the consent was stored with.",
                exception);
        }

        return plaintext;
    }
}
