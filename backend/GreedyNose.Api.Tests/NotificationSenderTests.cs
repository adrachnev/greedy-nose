using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using GreedyNose.Api.Notifications;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The send path (TRACER-02-NOTIFICATIONS.md, step 3) as far as it can be pinned without Firebase:
/// what goes on the wire, what each failure means, and how the debug endpoint reports it. Nothing here
/// touches the network — Firebase itself is what the real send on the phone proves.
/// </summary>
public class NotificationSenderTests
{
    // Shaped like a real FCM token (well over 32 characters, so the preview is a prefix) without being one.
    private const string FakeToken = "fake-token-1234567890:APA91b_abcdefghijklmnopqrstuvwxyz-XYZ";

    // --- The classifier: what a failure means -------------------------------------------------------

    /// <summary>
    /// FCM's own codes, written out. This table and the next one are the specification, so they are
    /// spelled out rather than derived: a test that computed its expectations from the implementation
    /// could not disagree with it.
    /// </summary>
    private static readonly Dictionary<MessagingErrorCode, SendOutcome> ExpectedByMessagingCode = new()
    {
        // The only "drop this token" code.
        [MessagingErrorCode.Unregistered] = SendOutcome.TokenNoLongerValid,
        // Looks like a dead token and is not: the key and the token belong to different projects, so a
        // wrong-project key would make EVERY token say this. Pruning on it would empty the list.
        [MessagingErrorCode.SenderIdMismatch] = SendOutcome.Rejected,
        [MessagingErrorCode.InvalidArgument] = SendOutcome.Rejected,
        // Not Android's problem and not self-healing: see FirebaseNotificationSender.Classify.
        [MessagingErrorCode.ThirdPartyAuthError] = SendOutcome.Rejected,
        [MessagingErrorCode.Unavailable] = SendOutcome.Transient,
        [MessagingErrorCode.Internal] = SendOutcome.Transient,
        [MessagingErrorCode.QuotaExceeded] = SendOutcome.Transient,
    };

    /// <summary>
    /// The general codes (in effect the HTTP status), which decide only when FCM sent no code of its own.
    /// </summary>
    private static readonly Dictionary<ErrorCode, SendOutcome> ExpectedByGeneralCode = new()
    {
        // The service or the path to it — not the request.
        [ErrorCode.Unknown] = SendOutcome.Transient, // what a network failure is reported as
        [ErrorCode.Internal] = SendOutcome.Transient,
        [ErrorCode.Unavailable] = SendOutcome.Transient,
        [ErrorCode.ResourceExhausted] = SendOutcome.Transient,
        [ErrorCode.DeadlineExceeded] = SendOutcome.Transient,
        // Everything else is permanent until a human looks: a bad request, a revoked key (401), a
        // disabled API (403), a 404 without FCM's UNREGISTERED detail.
        [ErrorCode.InvalidArgument] = SendOutcome.Rejected,
        [ErrorCode.FailedPrecondition] = SendOutcome.Rejected,
        [ErrorCode.OutOfRange] = SendOutcome.Rejected,
        [ErrorCode.Unauthenticated] = SendOutcome.Rejected,
        [ErrorCode.PermissionDenied] = SendOutcome.Rejected,
        [ErrorCode.NotFound] = SendOutcome.Rejected,
        [ErrorCode.Conflict] = SendOutcome.Rejected,
        [ErrorCode.Aborted] = SendOutcome.Rejected,
        [ErrorCode.AlreadyExists] = SendOutcome.Rejected,
        [ErrorCode.Cancelled] = SendOutcome.Rejected,
        [ErrorCode.DataLoss] = SendOutcome.Rejected,
    };

    public static TheoryData<MessagingErrorCode, SendOutcome> MessagingCodes()
    {
        var data = new TheoryData<MessagingErrorCode, SendOutcome>();
        foreach (var (code, outcome) in ExpectedByMessagingCode)
        {
            data.Add(code, outcome);
        }

        return data;
    }

    public static TheoryData<ErrorCode, SendOutcome> GeneralCodes()
    {
        var data = new TheoryData<ErrorCode, SendOutcome>();
        foreach (var (code, outcome) in ExpectedByGeneralCode)
        {
            data.Add(code, outcome);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MessagingCodes))]
    public void Each_messaging_error_code_lands_in_its_bucket(MessagingErrorCode code, SendOutcome expected)
    {
        Assert.Equal(expected, FirebaseNotificationSender.Classify(code, errorCode: null));
    }

    /// <summary>
    /// FCM's own code is the more specific answer, so it wins over the HTTP-level one: an UNREGISTERED
    /// arrives as a 404 (general <c>NotFound</c>, which alone would be Rejected) and QUOTA_EXCEEDED as
    /// a 429 — and neither may be decided by the general code.
    /// </summary>
    [Fact]
    public void The_messaging_code_decides_whatever_the_general_code_says()
    {
        var general = Enum.GetValues<ErrorCode>().Select(c => (ErrorCode?)c).Append(null).ToList();

        foreach (var (code, expected) in ExpectedByMessagingCode)
        {
            Assert.All(general, errorCode =>
                Assert.True(
                    expected == FirebaseNotificationSender.Classify(code, errorCode),
                    $"{code} with general code {errorCode?.ToString() ?? "none"} should be {expected}"));
        }
    }

    [Theory]
    [MemberData(nameof(GeneralCodes))]
    public void Without_a_messaging_code_the_general_error_code_decides(ErrorCode code, SendOutcome expected)
    {
        Assert.Equal(expected, FirebaseNotificationSender.Classify(messagingCode: null, code));
    }

    /// <summary>
    /// Nothing to go on at all (a failure that carried no code of either kind). It says nothing about
    /// the token or the request, so it is retryable and never a reason to drop a token.
    /// </summary>
    [Fact]
    public void No_code_at_all_is_transient()
    {
        Assert.Equal(SendOutcome.Transient, FirebaseNotificationSender.Classify(messagingCode: null, errorCode: null));
    }

    /// <summary>
    /// A code this table has never heard of — a value a newer SDK adds — must not be treated as
    /// success and must not be a reason to delete a token. It is Rejected: logged at error level, so
    /// the gap in the table gets seen. Both layers, and an unknown messaging code beats even a
    /// transient-looking general one.
    /// </summary>
    [Fact]
    public void An_unknown_code_is_rejected_never_sent_and_never_a_reason_to_delete()
    {
        Assert.Equal(SendOutcome.Rejected, FirebaseNotificationSender.Classify((MessagingErrorCode)999, errorCode: null));
        Assert.Equal(SendOutcome.Rejected, FirebaseNotificationSender.Classify((MessagingErrorCode)999, ErrorCode.Unavailable));
        Assert.Equal(SendOutcome.Rejected, FirebaseNotificationSender.Classify(messagingCode: null, (ErrorCode)999));
    }

    /// <summary>
    /// The two tables are written by hand, so an SDK upgrade that adds a code would slip past them
    /// into the catch-all unnoticed. This makes the upgrade fail here instead, and the fix is a
    /// decision: which bucket is the new code in?
    /// </summary>
    [Fact]
    public void Every_code_the_SDK_defines_has_an_explicit_decision_in_the_tables()
    {
        Assert.Equal(Enum.GetValues<MessagingErrorCode>().ToHashSet(), ExpectedByMessagingCode.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<ErrorCode>().ToHashSet(), ExpectedByGeneralCode.Keys.ToHashSet());
    }

    /// <summary>
    /// The two invariants step 6's pruning and bookkeeping lean on, checked over every combination
    /// rather than the rows above: a failure is never "sent", and exactly one combination — FCM's own
    /// UNREGISTERED — may ever lead to a token being dropped.
    /// </summary>
    [Fact]
    public void Across_every_combination_only_unregistered_means_drop_the_token_and_nothing_is_sent()
    {
        var messaging = Enum.GetValues<MessagingErrorCode>().Select(c => (MessagingErrorCode?)c)
            .Append(null).Append((MessagingErrorCode)999).ToList();
        var general = Enum.GetValues<ErrorCode>().Select(c => (ErrorCode?)c)
            .Append(null).Append((ErrorCode)999).ToList();

        foreach (var m in messaging)
        {
            foreach (var g in general)
            {
                var outcome = FirebaseNotificationSender.Classify(m, g);

                Assert.NotEqual(SendOutcome.Sent, outcome);
                Assert.Equal(m == MessagingErrorCode.Unregistered, outcome == SendOutcome.TokenNoLongerValid);
            }
        }
    }

    [Fact]
    public void A_failed_result_cannot_claim_to_be_sent()
    {
        Assert.Throws<ArgumentException>(() => SendResult.Failed(SendOutcome.Sent, "whatever"));
    }

    // --- The message builder: what goes on the wire -------------------------------------------------

    [Fact]
    public void The_message_carries_the_token_title_and_body_exactly()
    {
        // Umlauts, a euro sign and padding: the builder must not "tidy" the text — the push wording
        // (R12a, "49,00 €") is decided elsewhere and arrives ready to show.
        var message = FirebaseNotificationSender.BuildMessage(new PushMessage(FakeToken, " Bäckerei Müller · 49,00 € ", "Over your limit of 30,00 €."));

#pragma warning disable CS0618 // Message.Token — see the PRAGMATIC note in BuildMessage.
        Assert.Equal(FakeToken, message.Token);
#pragma warning restore CS0618
        Assert.Equal(" Bäckerei Müller · 49,00 € ", message.Notification.Title);
        Assert.Equal("Over your limit of 30,00 €.", message.Notification.Body);
    }

    /// <summary>
    /// A Normal-priority message waits out Doze, and a late alert is this product's worst failure.
    /// </summary>
    [Fact]
    public void The_message_is_high_priority_so_a_dozing_phone_wakes_for_it()
    {
        var message = FirebaseNotificationSender.BuildMessage(new PushMessage(FakeToken, "t", "b"));

        Assert.Equal(Priority.High, message.Android.Priority);
    }

    [Fact]
    public void The_message_expires_after_one_hour_because_a_stale_alert_is_worthless()
    {
        var message = FirebaseNotificationSender.BuildMessage(new PushMessage(FakeToken, "t", "b"));

        Assert.Equal(TimeSpan.FromHours(1), message.Android.TimeToLive);
    }

    [Fact]
    public void The_message_has_no_data_payload_yet()
    {
        var message = FirebaseNotificationSender.BuildMessage(new PushMessage(FakeToken, "t", "b"));

        Assert.Null(message.Data);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_is_the_callers_bug_and_is_refused_before_anything_is_sent(string token)
    {
        Assert.Throws<ArgumentException>(() => FirebaseNotificationSender.BuildMessage(new PushMessage(token, "t", "b")));
    }

    // --- Startup: the key must fail loudly, with the fix, and never echo itself ---------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_key_path_stops_startup_with_the_command_that_fixes_it(string? path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseAppFactory.GetOrCreate(new FirebaseOptions { ServiceAccountPath = path! }));

        Assert.Contains("dotnet user-secrets set \"Firebase:ServiceAccountPath\"", ex.Message);
    }

    [Fact]
    public void A_key_path_that_points_nowhere_stops_startup_and_names_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"no-such-firebase-key-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseAppFactory.GetOrCreate(new FirebaseOptions { ServiceAccountPath = path }));

        Assert.Contains(path, ex.Message);
        Assert.Contains("dotnet user-secrets set \"Firebase:ServiceAccountPath\"", ex.Message);
    }

    /// <summary>
    /// A file that exists but is not a service-account key (a truncated download, or a user
    /// credential) must fail at startup too — and the error goes to the console and the log, so it
    /// must not repeat what was in the file.
    /// </summary>
    [Theory]
    [InlineData("not json at all — SECRET-MARKER")]
    [InlineData("""{ "type": "authorized_user", "client_secret": "SECRET-MARKER" }""")]
    public void A_file_that_is_not_a_service_account_key_stops_startup_without_echoing_its_contents(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-firebase-key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                FirebaseAppFactory.GetOrCreate(new FirebaseOptions { ServiceAccountPath = path }));

            Assert.DoesNotContain("SECRET-MARKER", ex.ToString());
            Assert.Contains("dotnet user-secrets set \"Firebase:ServiceAccountPath\"", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

}
