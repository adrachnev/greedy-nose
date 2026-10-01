namespace GreedyNose.Api.Notifications;

/// <summary>
/// One push to one device. Text only for now: no data payload, no tap action (R12's navigation half
/// is deferred — TRACER-02-NOTIFICATIONS.md, "Deliberately out of scope").
/// </summary>
/// <param name="Token">The FCM registration token of the target install. Non-blank; never logged whole.</param>
public sealed record PushMessage(string Token, string Title, string Body);

/// <summary>
/// What a send came to, in the terms the caller acts on — not FCM's vocabulary. Each value is a
/// different instruction to whoever owns the token list and the retry policy (step 6's dispatcher):
/// </summary>
public enum SendOutcome
{
    /// <summary>FCM accepted the message. The one outcome that means "record it as sent" — accepted is not delivered, but it is all the API ever promises.</summary>
    Sent,

    /// <summary>
    /// FCM answered <c>UNREGISTERED</c>: the app was uninstalled or its token rotated, so this token
    /// will never work again. The token list should drop it. This is the <b>only</b> outcome that
    /// means "delete the token" — every other failure can be our fault (see <see cref="Rejected"/>),
    /// and pruning on it would turn a bug into an empty token list and silently missed alerts.
    /// </summary>
    TokenNoLongerValid,

    /// <summary>
    /// The request itself was refused and repeating it unchanged will not help: a malformed token or
    /// payload (<c>InvalidArgument</c>), a credential or project fault (a key Google refuses, a 401 or
    /// 403, <c>SenderIdMismatch</c> — the key and the token belong to different Firebase projects, so
    /// with a wrong-project key <em>every</em> token would say so), or anything the classifier does
    /// not recognise (see <see cref="FirebaseNotificationSender.Classify"/>). Needs a human, so it is
    /// logged at error level. It is deliberately <b>not</b> grounds to delete the token: the fault
    /// may be our own bug or configuration.
    /// </summary>
    Rejected,

    /// <summary>
    /// A failure that says nothing about the token or the request: FCM or the path to it is down,
    /// overloaded or slow (<c>Unavailable</c>, <c>Internal</c>, <c>QuotaExceeded</c>, a timeout, a
    /// network failure), or an exception nobody anticipated (logged at error level). Try again later;
    /// never delete the token.
    /// </summary>
    Transient,
}

/// <summary>
/// The result of one <see cref="INotificationSender.SendAsync"/>. Build it through <see cref="Accepted"/>
/// or <see cref="Failed"/> so the invariant holds: <see cref="MessageId"/> is set exactly when the
/// outcome is <see cref="SendOutcome.Sent"/>.
/// </summary>
/// <param name="Outcome">What the caller should do next.</param>
/// <param name="MessageId">FCM's id for the accepted message; <c>null</c> for every failure.</param>
/// <param name="Reason">
/// Why a send failed, in FCM's own words (its error code name) or, for a failure that never reached
/// FCM, a short description ("the request timed out", "Google's token endpoint answered 400
/// (invalid_grant)", "unexpected NullReferenceException"). Never contains a whole device token.
/// <c>null</c> for <see cref="SendOutcome.Sent"/>. For diagnosis and logs — callers branch on
/// <see cref="Outcome"/>, never on this string.
/// </param>
public sealed record SendResult(SendOutcome Outcome, string? MessageId, string? Reason)
{
    public static SendResult Accepted(string messageId) => new(SendOutcome.Sent, messageId, null);

    public static SendResult Failed(SendOutcome outcome, string reason)
    {
        if (outcome == SendOutcome.Sent)
        {
            throw new ArgumentException("A failure cannot have the outcome Sent.", nameof(outcome));
        }

        return new SendResult(outcome, null, reason);
    }
}

/// <summary>
/// Sends one push. The seam between "decide to notify" (rule engine, step 5) and "talk to Firebase":
/// the ingestion worker (step 6) and its tests depend on this, so they can run with a fake and no
/// network.
///
/// Contract: every delivery failure is a <see cref="SendResult"/>, not an exception — including a
/// timeout, a refused credential and an exception nobody anticipated — so a caller sending to several
/// tokens is never cut short by one bad one, and a worker's poll never dies on a push. What still
/// throws: cancellation the caller asked for, as <see cref="OperationCanceledException"/> (a timeout
/// is <em>not</em> that — it is <see cref="SendOutcome.Transient"/>), and a malformed
/// <see cref="PushMessage"/> (a blank token), which is the caller's bug and throws
/// <see cref="ArgumentException"/>.
/// </summary>
public interface INotificationSender
{
    Task<SendResult> SendAsync(PushMessage message, CancellationToken ct);
}
