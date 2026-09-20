namespace GreedyNose.Api.Data;

/// <summary>
/// A booked charge the ingestion worker has already seen (R10b). Its presence here is what makes
/// a fetched debit "not new", so it is the memory behind "notify once".
/// </summary>
public sealed class Debit
{
    public Guid UserId { get; set; }

    /// <summary>
    /// The debit identity <c>TransactionMapper</c> resolved — <c>(account, entry_reference)</c>, or its
    /// composite fallback — stored verbatim as text. Never <c>transaction_id</c>, which the bank may
    /// change between fetches.
    /// </summary>
    public required string Id { get; set; }

    public required string PayeeId { get; set; }

    public decimal AmountEUR { get; set; }

    /// <summary>
    /// UTC only: Npgsql refuses to write a <see cref="DateTimeOffset"/> with a non-zero offset to
    /// <c>timestamptz</c>, which is what we want — the mapper already normalises every booking to UTC.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>False when the bank booked to a day only — <see cref="Timestamp"/> is then midnight padding, not a time.</summary>
    public bool HasTime { get; set; }

    /// <summary>The app's payment-type label ('Card payment', …), as the mapper produced it.</summary>
    public required string PaymentType { get; set; }

    /// <summary>Empty for most charges — remittance text was on 4 of 100 rows in the first dump.</summary>
    public string Reference { get; set; } = "";

    /// <summary>
    /// <c>ConnectedAccount.Key</c> (the IBAN), not the account uid, which changes on every consent.
    /// Lets step 6 tell "first sync for this account" (bootstrap, silent) from steady state.
    /// </summary>
    public required string AccountKey { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
}
