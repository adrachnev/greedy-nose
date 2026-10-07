namespace GreedyNose.Api.Data;

/// <summary>
/// The live consent of one bank for one user (Story 1.7): one row per <c>(UserId, AspspName)</c>, so
/// reconnecting a bank replaces its row and another bank's row is never touched.
///
/// The session id and everything else that identifies an account (IBANs, uids) are stored only as
/// AES-256-GCM ciphertext (<c>ConsentCipher</c>) — the session id reads a real bank account, so the
/// database never holds it in plaintext (NFR7). The plaintext columns carry no account data: the user,
/// the bank's public name and a timestamp.
/// </summary>
public sealed class BankConsent
{
    public Guid UserId { get; set; }

    public required string AspspName { get; set; }

    /// <summary>The Enable Banking session id, encrypted.</summary>
    public required byte[] SessionIdCipher { get; set; }

    /// <summary>JSON of the rest of the consent (accounts, connected-at, expiry), encrypted.</summary>
    public required byte[] PayloadCipher { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
