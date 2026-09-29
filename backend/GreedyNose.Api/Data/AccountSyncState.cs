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
}
