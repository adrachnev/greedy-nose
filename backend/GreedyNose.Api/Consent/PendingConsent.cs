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
}
