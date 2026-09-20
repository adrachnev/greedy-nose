using GreedyNose.Api.Domain;

namespace GreedyNose.Api.Data;

/// <summary>
/// The user's rule for one payee (R4) — the server-side copy the rule engine reads. The app's local
/// store stays the source of truth (R6); this is synced from it (step 4).
///
/// Keyed on the payee, so a payee has at most one rule by construction. The key is also a composite
/// foreign key to <see cref="Payee"/>, which means a rule cannot be stored for a payee the database
/// has never seen — step 4 has to deal with that.
/// </summary>
public sealed class Rule
{
    public Guid UserId { get; set; }

    public required string PayeeId { get; set; }

    /// <summary>
    /// <c>required</c> because the enum's default is <c>Good</c> (value 0): a rule built without this
    /// set would be stored as good and quietly mute the payee, the opposite of the opt-out trust
    /// model. The enum order stays as is so the stored values never depend on it.
    /// </summary>
    public required Classification Classification { get; set; }

    /// <summary>
    /// The limit, in euros. Null means no limit (R4a). Only meaningful while good (R5), but kept when
    /// the payee is marked bad so marking them good again brings it back (R8a) — hence not cleared here.
    /// </summary>
    public decimal? AmountEUR { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
