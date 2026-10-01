using System.Buffers.Text;
using System.Security.Cryptography;
using GreedyNose.Api.Ingestion;

namespace GreedyNose.Api.Consent;

/// <summary>
/// Random one-time secrets (link token, OAuth state) and the only form in which they are stored.
/// Neither the raw value nor its hash is ever logged.
/// </summary>
public static class OneTimeSecret
{
    /// <summary>256 bits from the OS CSPRNG, URL-safe (no padding), so it survives a query string unescaped.</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 hex. Same one-way function as the session fingerprint — one place defines it.</summary>
    public static string Hash(string secret) => SessionFingerprint.Of(secret);
}
