namespace GreedyNose.Api.Data;

/// <summary>
/// A push we sent for a debit. One row per debit at most — see the unique index in
/// <see cref="GreedyNoseDbContext"/> — which is what guarantees R11 (no double send) even across
/// restarts or two overlapping poll ticks.
/// </summary>
public sealed class NotificationLogEntry
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string DebitId { get; set; }

    public DateTimeOffset SentAt { get; set; }

    /// <summary>Why the debit was bad (R12a) — the reason code the rule engine returned; defined in step 5.</summary>
    public required string Reason { get; set; }
}
