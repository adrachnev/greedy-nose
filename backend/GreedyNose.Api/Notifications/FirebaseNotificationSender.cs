using System.Net;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2.Responses;

namespace GreedyNose.Api.Notifications;

/// <summary>
/// <see cref="INotificationSender"/> over <c>FirebaseMessaging.SendAsync</c>. The only class that
/// speaks Firebase: the rest of the backend sees <see cref="SendResult"/>.
///
/// The two decisions that are not plumbing are pure functions on this class —
/// <see cref="BuildMessage"/> (what goes on the wire) and <see cref="Classify"/> (what a failure
/// means) — so both are tested without Firebase and without a network. The plumbing between them
/// (<see cref="SendAsync"/>) is tested offline too, against a stubbed HTTP layer.
/// </summary>
public sealed class FirebaseNotificationSender : INotificationSender
{
    /// <summary>
    /// How long FCM keeps trying to deliver a message to a phone that is offline. An alert about a
    /// charge that is hours old is worthless — worse than none, since the user cannot tell it is
    /// stale — so this is short. (FCM's own default is four weeks.)
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(1);

    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FirebaseNotificationSender> _logger;

    public FirebaseNotificationSender(FirebaseMessaging messaging, ILogger<FirebaseNotificationSender> logger)
    {
        _messaging = messaging;
        _logger = logger;
    }

    /// <summary>
    /// Every way a send can fail comes back as a <see cref="SendResult"/> — not only the ones Firebase
    /// reports as a <see cref="FirebaseMessagingException"/>. The SDK also lets a raw
    /// <see cref="TokenResponseException"/> through (a revoked key fails at the token fetch, before FCM
    /// is ever asked), a <see cref="NullReferenceException"/> on any error response with an empty body
    /// (from FCM or from Google's token endpoint, whatever the status), and a
    /// <see cref="TaskCanceledException"/> on a request timeout. Each of those, uncaught, would end a
    /// caller's loop over several tokens — and the step 6 worker's whole poll.
    ///
    /// The one thing that still propagates is cancellation the caller asked for.
    /// </summary>
    public async Task<SendResult> SendAsync(PushMessage push, CancellationToken ct)
    {
        // Built before the try: a blank token throws ArgumentException here, which is the caller's bug
        // and must not be dressed up as a delivery outcome.
        var message = BuildMessage(push);
        var preview = TokenPreview.Of(push.Token);

        try
        {
            var messageId = await _messaging.SendAsync(message, ct);

            _logger.LogInformation("Push accepted by FCM — {TokenPreview} → {MessageId}", preview, messageId);
            return SendResult.Accepted(messageId);
        }
        catch (Exception ex)
        {
            // The one exception that is not a failure: cancellation the caller asked for. It must
            // reach the caller — as itself, even if the SDK wrapped it in something else on the way
            // out. A request timeout is also an OperationCanceledException (HttpClient throws
            // TaskCanceledException) but arrives with the caller's token still live, so it falls
            // through: it looks like shutdown and is not.
            ct.ThrowIfCancellationRequested();

            var failure = Diagnose(ex);

            // The exception object is never passed to the logger: its message is text from Google or
            // from the SDK, and the token is a credential. Scrubbed here, then logged as plain text.
            var detail = failure.Detail is null ? "(no detail)" : TokenPreview.Redact(failure.Detail, push.Token);

            switch (failure)
            {
                // Something we did not anticipate: loud, whatever bucket it landed in.
                case { Unexpected: true }:
                    _logger.LogError(
                        "Unexpected failure sending a push ({Reason}) — {TokenPreview}: {Detail}",
                        failure.Reason, preview, detail);
                    break;

                case { Outcome: SendOutcome.TokenNoLongerValid }:
                    _logger.LogWarning(
                        "FCM says this token is no longer valid ({Reason}) — {TokenPreview}. The token list should drop it.",
                        failure.Reason, preview);
                    break;

                case { Outcome: SendOutcome.Rejected }:
                    // Loud on purpose: our request, our credentials or our classifier table is wrong,
                    // and nothing else will notice — a rejected push is a charge the user never hears about.
                    _logger.LogError(
                        "Push rejected, and repeating it unchanged will not help ({Reason}) — {TokenPreview}: {Detail}",
                        failure.Reason, preview, detail);
                    break;

                default:
                    _logger.LogWarning(
                        "Push could not be sent right now ({Reason}) — {TokenPreview}: {Detail}",
                        failure.Reason, preview, detail);
                    break;
            }

            return SendResult.Failed(failure.Outcome, failure.Reason);
        }
    }

    /// <summary>What went wrong, in the terms the log and the <see cref="SendResult"/> need.</summary>
    /// <param name="Detail">The exception's own message, unscrubbed — never log it without <see cref="TokenPreview.Redact"/>.</param>
    /// <param name="Unexpected">True when none of the SDK's documented failure shapes matched.</param>
    private sealed record Failure(SendOutcome Outcome, string Reason, string? Detail, bool Unexpected = false);

    private static Failure Diagnose(Exception ex) => ex switch
    {
        FirebaseMessagingException fcm => new Failure(Classify(fcm.MessagingErrorCode, fcm.ErrorCode), Describe(fcm), fcm.Message),

        // Google's token endpoint would not give us an access token, so FCM was never reached. Usually
        // that is a verdict on the service-account key (revoked, deleted, wrong clock…: 400
        // invalid_grant, 401), which does not heal by retrying — Rejected. The exception: 429 and 5xx
        // (including a proxy's non-JSON 502 page) are the endpoint being throttled or down, which says
        // nothing about our credentials — Transient. The exception is Google's and carries no key
        // material.
        TokenResponseException token => new Failure(
            token.StatusCode is { } status && (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
                ? SendOutcome.Transient
                : SendOutcome.Rejected,
            DescribeTokenEndpoint(token),
            token.Message),

        // No answer inside the HTTP timeout; says nothing about the token or the request.
        OperationCanceledException => new Failure(SendOutcome.Transient, "the request timed out", null),

        // Anything else — above all the SDK's own NullReferenceException, which it throws on ANY error
        // response with an empty body (observed for FCM at 400, 401, 403, 404, 429, 500, 502 and 503,
        // and for the token endpoint at 400, 429 and 503). The HTTP status is lost with it, so an
        // empty-bodied *permanent* failure (a proxy's bare 401, 403 or 404) lands here as Transient
        // instead of Rejected. Acceptable, and stated: it is logged at error level, so it is seen, and
        // it never leads to a token being deleted. Not retried blindly forever either — the caller's
        // policy decides.
        _ => new Failure(SendOutcome.Transient, $"unexpected {ex.GetType().Name}", ex.Message, Unexpected: true),
    };

    /// <summary>
    /// What Google's token endpoint said, for the reason: accurate for a verdict on the key
    /// ("answered 400 (invalid_grant)") and for an outage ("answered 503 (…)") alike — the status is
    /// what tells them apart.
    /// </summary>
    private static string DescribeTokenEndpoint(TokenResponseException ex)
    {
        var error = ex.Error?.Error ?? "no error code";

        return ex.StatusCode is { } status
            ? $"Google's token endpoint answered {(int)status} ({error})"
            : $"Google's token endpoint failed the request ({error})";
    }

    /// <summary>
    /// <c>(token, title, body) → Message</c>. Pure.
    ///
    /// Priority is High because this product has one failure that matters: a late or missing alert.
    /// A Normal-priority message is held back while the phone is in Doze — the state a phone spends
    /// most of the night and much of the day in — and a bad charge would show up when the owner next
    /// picks the phone up. High wakes it.
    ///
    /// The time-to-live is <see cref="TimeToLive"/>. There is no data payload yet (R12's tap-to-open
    /// is deferred); a notification message with only title and body is displayed by the OS itself
    /// while the app is in the background.
    /// </summary>
    public static Message BuildMessage(PushMessage push)
    {
        // Firebase would refuse this too, but with an exception thrown from inside SendAsync; failing
        // here keeps the contract on our side of the seam.
        ArgumentException.ThrowIfNullOrWhiteSpace(push.Token);

        // PRAGMATIC: FirebaseAdmin 3.6.0 marks Message.Token obsolete in favour of Message.Fid (a
        // Firebase Installation ID). Our app registers an FCM *registration token* (getToken() in
        // @react-native-firebase/messaging), which is a different identifier — Fid does not accept it —
        // and the SDK still documents registration tokens as supported. Moving to FIDs is an app
        // change (a different client API, a different DeviceTokens value) and a decision for the
        // owner, not something to slip into step 3. Scoped to this one assignment so any *other*
        // deprecation still warns. Revisit when the SDK removes Token — see TODO.md.
#pragma warning disable CS0618
        return new Message
        {
            Token = push.Token,
            Notification = new Notification { Title = push.Title, Body = push.Body },
            Android = new AndroidConfig { Priority = Priority.High, TimeToLive = TimeToLive },
        };
#pragma warning restore CS0618
    }

    /// <summary>
    /// <c>(MessagingErrorCode?, ErrorCode?) → SendOutcome</c>. Pure; the table lives in one place so
    /// step 6 can lean on it. Two layers, most specific first.
    ///
    /// <b>1. FCM's own code, when the answer carried one, decides — whatever the general code says.</b>
    /// <list type="bullet">
    /// <item><c>Unregistered</c> → <see cref="SendOutcome.TokenNoLongerValid"/>. The <b>only</b> code that
    /// means "drop this token": the app was uninstalled or the token rotated.</item>
    /// <item><c>InvalidArgument</c>, <c>SenderIdMismatch</c>, <c>ThirdPartyAuthError</c> →
    /// <see cref="SendOutcome.Rejected"/>. <c>SenderIdMismatch</c> looks like "this token is dead"
    /// and is not: Firebase's guidance is that the credential and the token belong to different
    /// projects, and with a wrong-project key <em>every</em> token would answer it — pruning on it would
    /// empty the token list. <c>ThirdPartyAuthError</c> is an APNs / web-push credential problem: it
    /// does not apply to Android and would be our own configuration if it appeared.</item>
    /// <item><c>Unavailable</c>, <c>Internal</c>, <c>QuotaExceeded</c> → <see cref="SendOutcome.Transient"/>.</item>
    /// <item>A code this table has never heard of (a newer SDK adds one) → <see cref="SendOutcome.Rejected"/>.
    /// Never <c>Sent</c>, never a reason to delete a token. Unknown means the table has a gap, and the
    /// only response that closes a gap is a human seeing it, so it is logged at error level. The price
    /// is that a future <em>transient</em> code is not retried until someone adds it here; for a product whose
    /// worst failure is a silent one, a loud false alarm is cheaper than a quiet retry loop.</item>
    /// </list>
    ///
    /// <b>2. No messaging code — then the general <see cref="ErrorCode"/> (the HTTP status, in
    /// effect) decides.</b> A 400 with no FCM detail, a 401 (revoked key) and a 403 (API disabled) all
    /// arrive this way, and each is permanent: retrying them forever at warning level would hide the
    /// fault.
    /// <list type="bullet">
    /// <item><c>Unknown</c> (what a network failure is reported as), <c>Internal</c>, <c>Unavailable</c>,
    /// <c>ResourceExhausted</c>, <c>DeadlineExceeded</c> → <see cref="SendOutcome.Transient"/>: the
    /// service or the path to it, not the request.</item>
    /// <item>Every other general code (<c>InvalidArgument</c>, <c>Unauthenticated</c>,
    /// <c>PermissionDenied</c>, <c>NotFound</c>…, and any a newer SDK adds) →
    /// <see cref="SendOutcome.Rejected"/>. A 404 <em>without</em> FCM's <c>UNREGISTERED</c> detail is
    /// not treated as a dead token, for the same reason as <c>SenderIdMismatch</c>.</item>
    /// <item><c>null</c> — no error code at all → <see cref="SendOutcome.Transient"/>; it says nothing.</item>
    /// </list>
    /// </summary>
    public static SendOutcome Classify(MessagingErrorCode? messagingCode, ErrorCode? errorCode) => messagingCode switch
    {
        MessagingErrorCode.Unregistered => SendOutcome.TokenNoLongerValid,

        MessagingErrorCode.Unavailable or MessagingErrorCode.Internal or MessagingErrorCode.QuotaExceeded
            => SendOutcome.Transient,

        // InvalidArgument, SenderIdMismatch, ThirdPartyAuthError — and anything unknown, by design.
        { } => SendOutcome.Rejected,

        null => errorCode switch
        {
            null or ErrorCode.Unknown or ErrorCode.Internal or ErrorCode.Unavailable
                or ErrorCode.ResourceExhausted or ErrorCode.DeadlineExceeded => SendOutcome.Transient,
            _ => SendOutcome.Rejected,
        },
    };

    /// <summary>
    /// The failure in FCM's vocabulary, for logs and <see cref="SendResult.Reason"/>: its messaging
    /// error code when it sent one, otherwise the general error code so that a "no code at all"
    /// failure still says what kind (a network error is <c>Unknown</c>, a revoked key is
    /// <c>Unauthenticated</c>).
    /// </summary>
    private static string Describe(FirebaseMessagingException ex) =>
        ex.MessagingErrorCode is { } code ? code.ToString() : $"no messaging error code (ErrorCode {ex.ErrorCode})";
}
