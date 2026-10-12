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

    // Poll bookkeeping (Story 3.2), plaintext on purpose: times, a short error code and a number — no
    // account data. Written by the worker after each bank, never by a reconnect (SaveAsync leaves them).

    /// <summary>When the worker last tried this bank (success or failure); a skipped tick does not count.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Exception type and a short code of the last failed attempt (at most <see cref="LastErrorMaxLength"/> chars), null after a success. Never a message text.</summary>
    public string? LastError { get; set; }

    public const int LastErrorMaxLength = 200;

    /// <summary>Minutes between polls of this bank; null = the default (6 h).</summary>
    public int? PollIntervalMinutes { get; set; }

    /// <summary>
    /// Hash of the dead session R19's "Bank connection expired" push was last accepted for (Story 4.1;
    /// moved here from <c>AccountSyncState</c> so it exists before any first sync finished). The push goes
    /// out only while this differs from the dead session's hash; a reconnect has a new hash, so no reset
    /// is needed (SaveAsync leaves this column alone). Set only once a device took the push. Null = never told.
    /// </summary>
    public string? ExpiryNotifiedSessionHash { get; set; }
}
