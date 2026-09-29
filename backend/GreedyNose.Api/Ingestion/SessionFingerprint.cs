using System.Security.Cryptography;
using System.Text;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// The only form of a consent session id that may be stored in the database. The session id is an
/// access credential (it reads a real account — which is why <c>consent.local.json</c> is gitignored),
/// and "did the user log in at the bank again?" only needs an equality check, so the runner persists
/// this hash and never the id. The hash is as sensitive as the id for logging purposes: never log either.
/// </summary>
public static class SessionFingerprint
{
    /// <summary>SHA-256 over the UTF-8 bytes, lowercase hex (64 characters).</summary>
    public static string Of(string sessionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
}
