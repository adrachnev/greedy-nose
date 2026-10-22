namespace GreedyNose.Api.Consent;

/// <summary>
/// One consent the owner is in the middle of starting (Story 1.4). Created by the authenticated
/// <c>POST /connect-links</c>, consumed once by <c>GET /connect?token=</c> (which mints the OAuth
/// <c>state</c> for the bank) and completed once by <c>GET /callback</c>.
///
/// Both secrets — the link token and the state — are stored only as SHA-256 hashes
/// (<see cref="OneTimeSecret"/>): the database never holds a value that can start or finish a
/// consent. Held in the database, not in memory, so a restart or a second instance does not lose or
/// duplicate a pending consent.
///
/// <see cref="Version"/> is the optimistic-concurrency token that makes "single use" atomic: two
/// requests presenting the same secret both read version N, and only one UPDATE ... WHERE Version = N
/// can win — the loser gets <c>DbUpdateConcurrencyException</c> and is refused. App-managed for the
/// same reason as <c>Payee.Version</c> (the in-memory test provider does not simulate Postgres' xmin).
/// </summary>
public sealed class PendingConsent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The bank, bound to this record — the callback takes it from here, never from a global.</summary>
    public required string AspspName { get; set; }

    public required string Country { get; set; }

    public required string PsuType { get; set; }

    /// <summary>SHA-256 hex of the one-time link token handed to the owner.</summary>
    public required string LinkTokenHash { get; set; }

    /// <summary>SHA-256 hex of the OAuth state sent to the bank; null until the link was used.</summary>
    public string? StateHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When <c>/connect</c> consumed the link token; null = not used yet.</summary>
    public DateTimeOffset? LinkUsedAt { get; set; }

    /// <summary>When <c>/callback</c> consumed the state; null = not completed yet.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Hard deadline for the whole flow, from creation; both steps refuse after it.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public int Version { get; set; }

    // Story 5.3: the row is also the attempt the app follows (its Id is the attempt id). No secret: the API key
    // protects the reads. Only short codes here, never a bank text.

    /// <summary>How the callback ended, see <see cref="ConnectOutcome"/>; null = still waiting for the bank.</summary>
    public string? Outcome { get; set; }

    /// <summary>A short kebab-case code (see <see cref="ConnectFailure"/>) when <see cref="Outcome"/> is failed.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// The session id and the euro accounts the owner can choose from, encrypted with <c>ConsentCipher</c>
    /// (associated data = row id and column, see <see cref="WaitingPayload"/>). Set only while the outcome is
    /// choose-account; deleted when the choice is made, cancelled or the deadline passes.
    /// </summary>
    public byte[]? WaitingPayload { get; set; }

    /// <summary>Until when the owner may choose an account; set together with <see cref="WaitingPayload"/>.</summary>
    public DateTimeOffset? ChoiceDeadline { get; set; }
}

/// <summary>The values of <see cref="PendingConsent.Outcome"/>.</summary>
public static class ConnectOutcome
{
    public const string ChooseAccount = "choose-account";
    public const string Saved = "saved";
    public const string Failed = "failed";
}

/// <summary>The values of <see cref="PendingConsent.FailureReason"/>: short codes, never a bank text.</summary>
public static class ConnectFailure
{
    public const string Cancelled = "cancelled";
    public const string BankError = "bank-error";
    public const string CodeNotTraded = "code-not-traded";
    public const string BankAnswerUnusable = "bank-answer-unusable";
    public const string StoreFailed = "store-failed";
    public const string NoSupportedAccount = "no-supported-account";
}
