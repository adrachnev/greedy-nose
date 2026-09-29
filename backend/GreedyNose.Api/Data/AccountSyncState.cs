namespace GreedyNose.Api.Data;

/// <summary>
/// Proof that a connected account's first sync (R25) finished. The row exists <em>only</em> once it
/// has — "no row" means "the first sync is still to do", so there is no nullable timestamp to
/// misread. This, not "do any Debits rows exist", is the mode signal: a first sync cut short after
/// writing some rows, or an account with no history at all, would both look like steady state to a
/// row-existence check and either alert the rest of the history or swallow the first real charge.
///
/// Named for what it holds, not <c>ConnectedAccount</c>, which is already the Enable Banking
/// record. <see cref="AccountKey"/> is the IBAN, never the bank's per-consent <c>uid</c> (see
/// <c>Debit.AccountKey</c>).
/// </summary>
public sealed class AccountSyncState
{
    public Guid UserId { get; set; }

    public required string AccountKey { get; set; }

    /// <summary>When the tick that finished the first sync committed — written in the same save as that tick's debits.</summary>
    public DateTimeOffset FirstSyncCompletedAt { get; set; }

    /// <summary>
    /// SHA-256 hex of the consent session id this account was last synced under — the hash, never the
    /// id (a credential; see <c>SessionFingerprint</c>). A different value on a later tick means the
    /// user logged in at the bank again: a reconnect. Null = the row predates this column, session
    /// unknown, adopted silently on the next tick (no backfill is possible: the id lives only in the
    /// consent file).
    /// </summary>
    public string? LastSessionIdHash { get; set; }
}
