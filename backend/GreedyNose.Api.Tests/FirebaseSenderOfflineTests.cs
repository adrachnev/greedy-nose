using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using GreedyNose.Api.Notifications;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The real <see cref="FirebaseNotificationSender"/> — the part of it that is not a pure function —
/// run against the real FirebaseAdmin SDK with its HTTP layer replaced by a stub, so what the SDK does
/// with an answer (or with no answer) is observed rather than assumed. NOTIFICATION-TRACER-BULLET.md,
/// step 3.
///
/// <b>Nothing here can reach the network or use a real key.</b> The stub handler is the terminal
/// handler of every HTTP client the SDK and the credential build, so no request can go anywhere else;
/// it also records — and refuses — any host that is not one of the two fake ones. The credentials are
/// an access-token string or a service account over a key generated for the test and thrown away.
/// Every test builds its own named <see cref="FirebaseApp"/> and deletes it, so none of them touches
/// <c>FirebaseApp.DefaultInstance</c> or another test's app.
/// </summary>
public class FirebaseSenderOfflineTests
{
    private const string FcmHost = "fcm.googleapis.com";
    private const string TokenHost = "oauth2.test.invalid";
    private const string ProjectId = "test-project";

    // Shaped like a real FCM token — long, with the ':' a real one carries — without being one.
    private const string FakeToken = "fake-token-1234567890:APA91b_abcdefghijklmnopqrstuvwxyz-XYZ_0123456789";

    private static readonly PushMessage Push = new(FakeToken, "Greedy Nose", "Test push from the backend");

    // --- What the stub answers with -------------------------------------------------------------------

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        // The SDK retries a 503 four times with exponential back-off (about 15 seconds) unless the answer says to
        // come back later than it is willing to wait — which makes it give up at once. Keeps the tests fast.
        response.Headers.TryAddWithoutValidation("Retry-After", "100000");
        return response;
    }

    private static HttpResponseMessage Sent(string messageId) =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { name = $"projects/{ProjectId}/messages/{messageId}" }));

    /// <summary>An FCM v1 error body: the HTTP-level status plus, optionally, FCM's own code in <c>details</c>.</summary>
    private static string FcmError(int code, string status, string? fcmErrorCode, string message = "stub error")
    {
        object error = fcmErrorCode is null
            ? new { code, message, status }
            : new
            {
                code,
                message,
                status,
                details = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                        ["errorCode"] = fcmErrorCode,
                    },
                },
            };

        return JsonSerializer.Serialize(new { error });
    }

    // --- The stub and the harness ------------------------------------------------------------------------

    private delegate Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct);

    private sealed class StubHandler(Respond respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Uri> _requests = [];
        private readonly List<string> _bodies = [];

        public IReadOnlyList<Uri> Requests { get { lock (_gate) return [.. _requests]; } }

        /// <summary>The JSON bodies POSTed to FCM, in order.</summary>
        public IReadOnlyList<string> FcmBodies { get { lock (_gate) return [.. _bodies]; } }

        /// <summary>Set the first time a request reaches the FCM host.</summary>
        public TaskCompletionSource FcmRequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (_gate) _requests.Add(uri);

            if (uri.Host is not (FcmHost or TokenHost))
            {
                // Recorded (Requests) and refused: a test that lets a request escape to any other host fails.
                throw new InvalidOperationException($"The test stub only answers for {FcmHost} and {TokenHost}, not {uri.Host}.");
            }

            if (uri.Host == FcmHost)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                lock (_gate) _bodies.Add(body);
                FcmRequestArrived.TrySetResult();
            }

            return await respond(request, ct);
        }
    }

    /// <summary>The SDK's <c>HttpClientFactory</c>, with the stub as the handler at the bottom of every client it builds.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    /// <summary>
    /// Wraps a factory so every client it builds makes a single attempt. Google's auth library retries a
    /// token request that came back 429, 500 or 503 three times with about three seconds of back-off
    /// before it throws; that is its behaviour, not ours, and nothing here classifies differently
    /// because of it — so the credential's clients skip the wait.
    /// </summary>
    private sealed class SingleAttemptClientFactory(Google.Apis.Http.IHttpClientFactory inner) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var client = inner.CreateHttpClient(args);
            client.MessageHandler.NumTries = 1;
            return client;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Text, Exception? Exception);

    /// <summary>Captures everything the sender logs — the rendered message and every structured argument.</summary>
    private sealed class CapturingLogger : ILogger<FirebaseNotificationSender>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var arguments = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";

            Entries.Add(new LogEntry(logLevel, $"{formatter(state, exception)} | {arguments}", exception));
        }

        public string All => string.Join("\n", Entries.Select(e => e.Text));
    }

    private sealed class Harness : IDisposable
    {
        private readonly FirebaseApp _app;

        public Harness(Respond respond, Func<HttpClientFactory, GoogleCredential>? credential = null)
        {
            Handler = new StubHandler(respond);
            var factory = new StubHttpClientFactory(Handler);

            // A unique name per test: never the default app, never another test's.
            _app = FirebaseApp.Create(
                new AppOptions
                {
                    Credential = credential?.Invoke(factory) ?? GoogleCredential.FromAccessToken("test-access-token"),
                    ProjectId = ProjectId,
                    HttpClientFactory = factory,
                },
                $"sender-test-{Guid.NewGuid():N}");

            Sender = new FirebaseNotificationSender(FirebaseMessaging.GetMessaging(_app), Log);
        }

        public StubHandler Handler { get; }

        public CapturingLogger Log { get; } = new();

        public FirebaseNotificationSender Sender { get; }

        public Task<SendResult> SendAsync(CancellationToken ct = default) => Sender.SendAsync(Push, ct);

        public void Dispose()
        {
            _app.Delete();

            // Nothing may have gone anywhere but the two fake hosts.
            Assert.All(Handler.Requests, uri => Assert.True(uri.Host is FcmHost or TokenHost, $"Unexpected host {uri.Host}"));
        }
    }

    // A fresh response per request: the SDK may retry, and a response can be read only once.
    private static Harness Answering(Func<HttpResponseMessage> response) =>
        new((_, _) => Task.FromResult(response()));

    /// <summary>An access method that throws while the request is being authorised.</summary>
    private sealed class ThrowingAccessMethod(Func<Exception> toThrow) : IAccessMethod
    {
        public string GetAccessToken(HttpRequestMessage request) => "test-access-token";

        public void Intercept(HttpRequestMessage request, string accessToken) => throw toThrow();
    }

    /// <summary>
    /// A credential that throws <paramref name="toThrow"/> while the request is being authorised — where
    /// a revoked key's <c>TokenResponseException</c> comes from too. Used for "some exception we did not
    /// anticipate" because the alternative, throwing from the HTTP handler, makes the SDK retry it four
    /// times with back-off (about 15 seconds, observed) before giving up; an exception raised here is
    /// outside that retry loop.
    /// </summary>
    private static Func<HttpClientFactory, GoogleCredential> FailingBeforeTheRequest(Func<Exception> toThrow) =>
        _ => GoogleCredential.FromAccessToken("test-access-token", new ThrowingAccessMethod(toThrow));

    // --- Success, and what actually goes over the wire -----------------------------------------------------

    [Fact]
    public async Task An_accepted_message_comes_back_as_sent_with_the_fcm_message_id()
    {
        using var h = Answering(() => Sent("0:1234567890"));

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal($"projects/{ProjectId}/messages/0:1234567890", result.MessageId);
        Assert.Null(result.Reason);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Information);
    }

    /// <summary>
    /// What the SDK actually serialises for our message. <c>Message.Token</c> is deprecated in this SDK
    /// version (see the PRAGMATIC note in <c>BuildMessage</c>): if a later version stops sending it, or
    /// stops sending priority or TTL, this is where it shows up — the builder's own tests only see the
    /// object, not the request.
    /// </summary>
    [Fact]
    public async Task The_request_the_sdk_sends_carries_the_token_the_text_high_priority_and_a_one_hour_ttl()
    {
        using var h = Answering(() => Sent("0:1"));

        await h.SendAsync();

        Assert.Contains(h.Handler.Requests, uri => uri.Host == FcmHost && uri.AbsolutePath == $"/v1/projects/{ProjectId}/messages:send");

        using var body = JsonDocument.Parse(Assert.Single(h.Handler.FcmBodies));
        var message = body.RootElement.GetProperty("message");
        Assert.Equal(FakeToken, message.GetProperty("token").GetString());
        Assert.Equal("Greedy Nose", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Test push from the backend", message.GetProperty("notification").GetProperty("body").GetString());
        Assert.Equal("high", message.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("3600s", message.GetProperty("android").GetProperty("ttl").GetString());
        Assert.False(message.TryGetProperty("data", out _), "no data payload yet");
    }

    // --- What each answer means -----------------------------------------------------------------------------

    public static TheoryData<int, string, SendOutcome, string> Answers() => new()
    {
        // FCM's own code, sent with the HTTP status that normally accompanies it.
        { 404, FcmError(404, "NOT_FOUND", "UNREGISTERED"), SendOutcome.TokenNoLongerValid, "Unregistered" },
        { 400, FcmError(400, "INVALID_ARGUMENT", "INVALID_ARGUMENT"), SendOutcome.Rejected, "InvalidArgument" },
        // Not a dead token: the key and the token belong to different projects.
        { 403, FcmError(403, "PERMISSION_DENIED", "SENDER_ID_MISMATCH"), SendOutcome.Rejected, "SenderIdMismatch" },
        { 503, FcmError(503, "UNAVAILABLE", "UNAVAILABLE"), SendOutcome.Transient, "Unavailable" },
        { 500, FcmError(500, "INTERNAL", "INTERNAL"), SendOutcome.Transient, "Internal" },
        { 429, FcmError(429, "RESOURCE_EXHAUSTED", "QUOTA_EXCEEDED"), SendOutcome.Transient, "QuotaExceeded" },

        // No FCM code, only the HTTP status: permanent faults must not be filed as "try again".
        { 400, FcmError(400, "INVALID_ARGUMENT", null), SendOutcome.Rejected, "ErrorCode InvalidArgument" },
        { 401, FcmError(401, "UNAUTHENTICATED", null), SendOutcome.Rejected, "ErrorCode Unauthenticated" },
        { 403, FcmError(403, "PERMISSION_DENIED", null), SendOutcome.Rejected, "ErrorCode PermissionDenied" },
        // A 404 without FCM's UNREGISTERED detail is not proof the token is dead.
        { 404, FcmError(404, "NOT_FOUND", null), SendOutcome.Rejected, "ErrorCode NotFound" },

        // An error response with an EMPTY body makes the SDK itself throw a NullReferenceException — at
        // any status, observed for 400, 401, 403, 404, 429, 500, 502 and 503 — and the status is lost
        // with it. The sender must still answer with a result, never let it out; it lands in the
        // "unexpected exception" arm as Transient. That includes the permanent statuses (a proxy's bare
        // 401 or 404): known, accepted, error-logged, never a reason to delete a token. The reason is
        // pinned so that an SDK fix — the status coming through again — shows up here.
        { 500, "", SendOutcome.Transient, "unexpected NullReferenceException" },
        { 503, "", SendOutcome.Transient, "unexpected NullReferenceException" },
        { 401, "", SendOutcome.Transient, "unexpected NullReferenceException" },
        { 404, "", SendOutcome.Transient, "unexpected NullReferenceException" },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task An_answer_from_fcm_lands_in_the_bucket_its_code_says(
        int status, string body, SendOutcome expected, string reasonContains)
    {
        using var h = Answering(() => Json((HttpStatusCode)status, body));

        var result = await h.SendAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.MessageId);
        Assert.Contains(reasonContains, result.Reason);

        // Whichever branch the failure took (dead token, rejected, transient, unexpected), the exception
        // object itself is never handed to the logger: its message is Firebase's text, and the token
        // is a credential.
        Assert.All(h.Log.Entries, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task A_rejected_push_is_logged_at_error_level_and_a_dead_token_at_warning()
    {
        using (var rejected = Answering(() => Json(HttpStatusCode.BadRequest, FcmError(400, "INVALID_ARGUMENT", "INVALID_ARGUMENT"))))
        {
            await rejected.SendAsync();
            Assert.Contains(rejected.Log.Entries, e => e.Level == LogLevel.Error);
        }

        using var dead = Answering(() => Json(HttpStatusCode.NotFound, FcmError(404, "NOT_FOUND", "UNREGISTERED")));
        await dead.SendAsync();
        Assert.DoesNotContain(dead.Log.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(dead.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    // --- Failures that never became a FirebaseMessagingException ---------------------------------------------

    /// <summary>
    /// A service-account key Google refuses: the SDK never reaches FCM and lets Google's own
    /// <c>TokenResponseException</c> out of the token fetch. The key here is generated for the test.
    /// </summary>
    private static Harness WithRevokedKey(HttpStatusCode tokenEndpointStatus, string tokenEndpointBody, out string keyBodyLine)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        keyBodyLine = pem.Split('\n')[1].Trim();

        return new Harness(
            (request, _) => Task.FromResult(request.RequestUri!.Host == TokenHost
                ? Json(tokenEndpointStatus, tokenEndpointBody)
                : throw new InvalidOperationException("FCM must not be reached when the token fetch fails.")),
            factory => new ServiceAccountCredential(
                new ServiceAccountCredential.Initializer("test-sa@test-project.iam.gserviceaccount.com", $"https://{TokenHost}/token")
                {
                    HttpClientFactory = new SingleAttemptClientFactory(factory),
                    Scopes = ["https://www.googleapis.com/auth/firebase.messaging"],
                }.FromPrivateKey(pem)).ToGoogleCredential());
    }

    [Fact]
    public async Task A_key_google_refuses_is_rejected_not_thrown_and_the_error_carries_no_key_material()
    {
        using var h = WithRevokedKey(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""",
            out var keyBodyLine);

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Rejected, result.Outcome);
        Assert.Contains("answered 400 (invalid_grant)", result.Reason);
        Assert.All(h.Handler.Requests, uri => Assert.Equal(TokenHost, uri.Host)); // FCM was never reached
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(keyBodyLine, h.Log.All);
        Assert.DoesNotContain(keyBodyLine, result.Reason);
    }

    public static TheoryData<int, string, SendOutcome, string> TokenEndpointAnswers() => new()
    {
        // A verdict on the key, in Google's JSON error shape: retrying will not help.
        { 401, """{"error":"invalid_client","error_description":"The OAuth client was not found."}""", SendOutcome.Rejected, "answered 401 (invalid_client)" },
        // Not JSON, so the exception carries only the status — still a 4xx, still Rejected.
        { 403, "<html><body>Forbidden</body></html>", SendOutcome.Rejected, "answered 403" },

        // The endpoint being throttled or down says nothing about our key, and filing it as
        // "rejected" would stop a working setup from retrying. Each row pins one edge of the rule
        // (429, 500 and above, and a proxy's non-JSON page).
        { 429, """{"error":"rate_limited"}""", SendOutcome.Transient, "answered 429 (rate_limited)" },
        { 500, """{"error":"backend_error"}""", SendOutcome.Transient, "answered 500 (backend_error)" },
        { 503, """{"error":"unavailable"}""", SendOutcome.Transient, "answered 503 (unavailable)" },
        { 502, "<html><body>Bad Gateway</body></html>", SendOutcome.Transient, "answered 502" },
    };

    /// <summary>
    /// The status check on <see cref="Google.Apis.Auth.OAuth2.Responses.TokenResponseException"/>: every
    /// row here reaches it (a body Google's library can parse as JSON, or a non-JSON one it reports with
    /// only the status), and the reason must say what the endpoint answered, so that a 429 or a 503 does
    /// not read as a refused credential.
    /// </summary>
    [Theory]
    [MemberData(nameof(TokenEndpointAnswers))]
    public async Task Googles_token_endpoint_says_rejected_for_a_verdict_on_the_key_and_transient_for_an_outage(
        int status, string body, SendOutcome expected, string reasonContains)
    {
        using var h = WithRevokedKey((HttpStatusCode)status, body, out _);

        var result = await h.SendAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Contains(reasonContains, result.Reason);
        Assert.All(h.Handler.Requests, uri => Assert.Equal(TokenHost, uri.Host)); // FCM was never reached

        // Not the "unexpected" arm: the SDK's own exception type was recognised.
        Assert.DoesNotContain("unexpected", result.Reason);
        Assert.Contains(
            h.Log.Entries,
            e => e.Level == (expected == SendOutcome.Rejected ? LogLevel.Error : LogLevel.Warning) && e.Text.Contains(result.Reason!));
        Assert.All(h.Log.Entries, e => Assert.Null(e.Exception));
    }

    /// <summary>
    /// What the theory above does NOT reach: Google's library throws a <see cref="NullReferenceException"/>
    /// on an error response with an empty body, whatever its status, so the status is lost and the
    /// failure lands in the "unexpected exception" arm — Transient, logged at error level — even for a
    /// 400 that is really a verdict on the key. Pinned so that an SDK fix shows up here.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task An_empty_bodied_answer_from_googles_token_endpoint_is_an_unexpected_exception_and_the_status_is_lost(int status)
    {
        using var h = WithRevokedKey((HttpStatusCode)status, "", out _);

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Transient, result.Outcome);
        Assert.Equal("unexpected NullReferenceException", result.Reason);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.All(h.Log.Entries, e => Assert.Null(e.Exception));
    }

    /// <summary>
    /// An HTTP timeout arrives as a <see cref="TaskCanceledException"/> while the caller's token is
    /// still live. It looks like shutdown and is not: it must come back as a result, not propagate.
    /// </summary>
    [Fact]
    public async Task A_timeout_is_transient_and_does_not_look_like_shutdown()
    {
        using var h = new Harness((_, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        var result = await h.SendAsync(CancellationToken.None);

        Assert.Equal(SendOutcome.Transient, result.Outcome);
        Assert.Contains("timed out", result.Reason);
    }

    [Fact]
    public async Task Cancellation_the_caller_asked_for_propagates_while_the_request_is_in_flight()
    {
        using var cts = new CancellationTokenSource();
        using var h = new Harness(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // the request hangs until the caller gives up
            throw new InvalidOperationException("unreachable");
        });

        var sending = h.SendAsync(cts.Token);
        await h.Handler.FcmRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning); // a shutdown is not a failure
    }

    [Fact]
    public async Task Cancellation_that_was_already_requested_propagates_too()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var h = Answering(() => Sent("0:1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.SendAsync(cts.Token));
    }

    /// <summary>
    /// If the SDK (or anything under it) turns the caller's cancellation into some other exception on
    /// the way out, it must still reach the caller as a cancellation — not be filed as a failed send,
    /// which would tell a shutting-down worker that a push was rejected.
    /// </summary>
    [Fact]
    public async Task Cancellation_that_surfaces_as_another_exception_still_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        using var h = new Harness(
            (_, _) => Task.FromResult(Sent("0:1")),
            FailingBeforeTheRequest(() =>
            {
                cts.Cancel(); // the caller gives up…
                return new InvalidOperationException("…and what comes out is not an OperationCanceledException");
            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.SendAsync(cts.Token));
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// Whatever else the SDK or the HTTP stack throws is an unknown, not a reason to end the caller's
    /// loop: a result, retryable, and logged at error level so that "we don't know what this is" is seen.
    /// </summary>
    [Fact]
    public async Task An_exception_nobody_anticipated_is_transient_and_logged_as_an_error()
    {
        using var h = new Harness(
            (_, _) => Task.FromResult(Sent("0:1")),
            FailingBeforeTheRequest(() => new InvalidOperationException("the stub blew up")));

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Transient, result.Outcome);
        Assert.Contains("unexpected InvalidOperationException", result.Reason);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error);
    }

    // --- The log never carries a whole device token -----------------------------------------------------------

    /// <summary>
    /// The redaction contract, end to end: a failure whose text echoes the whole token — from FCM, or
    /// from an exception — must leave no full token in any log line, structured argument or result. The
    /// preview must be there, or the test would pass by logging nothing.
    /// </summary>
    [Fact]
    public async Task An_error_body_that_echoes_the_whole_token_never_reaches_the_log_or_the_result()
    {
        using var h = Answering(() => Json(
            HttpStatusCode.BadRequest,
            FcmError(400, "INVALID_ARGUMENT", "INVALID_ARGUMENT", $"The registration token {FakeToken} is not a valid FCM registration token")));

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Rejected, result.Outcome);
        Assert.DoesNotContain(FakeToken, h.Log.All);
        Assert.DoesNotContain(FakeToken, result.Reason);
        Assert.Contains(TokenPreview.Of(FakeToken), h.Log.All);
        Assert.All(h.Log.Entries, e => Assert.Null(e.Exception)); // the exception object itself is never logged
    }

    [Fact]
    public async Task An_unexpected_exception_that_echoes_the_whole_token_never_reaches_the_log_or_the_result()
    {
        using var h = new Harness(
            (_, _) => Task.FromResult(Sent("0:1")),
            FailingBeforeTheRequest(() => new InvalidOperationException($"could not deliver to {FakeToken}")));

        var result = await h.SendAsync();

        Assert.Equal(SendOutcome.Transient, result.Outcome);
        Assert.DoesNotContain(FakeToken, h.Log.All);
        Assert.DoesNotContain(FakeToken, result.Reason);
        Assert.Contains(TokenPreview.Of(FakeToken), h.Log.All);
        Assert.All(h.Log.Entries, e => Assert.Null(e.Exception));
    }
}
