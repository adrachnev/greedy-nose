namespace GreedyNose.Api.Data;

/// <summary>
/// The state behind Story 2.10's operator mail: one row per user. Counts failed push attempts in a row
/// (a send to all tokens is one attempt; debit and summary pushes count, the expiry push does not) and
/// remembers where the incident stands, so "once per incident" survives restarts. Kept apart from
/// <see cref="PendingPush"/> on purpose: a mark's <c>Attempts</c> are per mark, not "in a row" across marks.
/// Holds outcome names only, nothing about payees, amounts or tokens.
/// </summary>
public sealed class PushHealth
{
    public Guid UserId { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>Start of the current streak (kept across a recovery whose follow-up mail is still pending).</summary>
    public DateTimeOffset? FirstFailureAt { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Failing outcome names of the last failed attempt (<c>Rejected</c>, <c>NoToken</c>, ...).</summary>
    public string? LastOutcome { get; set; }

    /// <summary>Set only after Brevo accepted the failure mail; null = not sent for this incident.</summary>
    public DateTimeOffset? FailureMailSentAt { get; set; }

    /// <summary>A push succeeded after a failure mail was sent; the follow-up mail is owed.</summary>
    public bool RecoveryMailPending { get; set; }

    public DateTimeOffset? RecoveredAt { get; set; }
}
