namespace GreedyNose.Api.Data;

public enum PendingPushKind
{
    /// <summary>The R12a alert of one bad debit that could not be delivered at ingestion time.</summary>
    Debit,

    /// <summary>The R20 reconnect summary that could not be delivered.</summary>
    ReconnectSummary,
}

/// <summary>
/// An alert we still owe (R11a, Story 2.9): the explicit mark that a push failed on its first attempt and is
/// retried on every later tick until it is sent or 24 h are over. It is written only at ingestion time,
/// in the same save as the debit it belongs to — never inferred from "bad debit without a NotificationLog
/// row" — so a debit that turns bad later through a rule edit (R10a) never gets one.
///
/// The text is frozen where it must not drift: the R12a reason for a debit mark, N and M for a summary.
/// A debit mark's title is built at send time from the payee's current name and the debit's amount.
/// An open mark has <see cref="EndedAt"/> null; a finished one stays for diagnosis. At most one mark
/// per debit (unique index). Attempts, last outcome and time are what Story 2.10's failure mail reads.
/// </summary>
public sealed class PendingPush
{
    /// <summary><see cref="EndReason"/> values.</summary>
    public const string EndedSent = "Sent";

    public const string EndedGaveUp = "GaveUp";

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public PendingPushKind Kind { get; set; }

    /// <summary>The debit this mark is for; null for a summary.</summary>
    public string? DebitId { get; set; }

    /// <summary>R12a text of a debit mark, frozen at creation; null for a summary.</summary>
    public string? Reason { get; set; }

    public int? SummaryNewCount { get; set; }

    public int? SummaryBadCount { get; set; }

    /// <summary>"Owed since" — the 24 h give-up window counts from here.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Includes the first, failed attempt at ingestion time.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Failing outcome names of the last attempt (<c>NoToken</c>, <c>Rejected</c>, <c>Transient</c>, ...), or <c>Sent</c>.</summary>
    public string? LastOutcome { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary><see cref="EndedSent"/> or <see cref="EndedGaveUp"/>; null while open.</summary>
    public string? EndReason { get; set; }
}
