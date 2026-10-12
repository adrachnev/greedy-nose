using System.Net;
using GreedyNose.Api.Alerts;
using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 2.10: the operator mail after three failed push attempts in a row. Same in-memory plumbing as
/// <c>PendingPushTests</c>, with a fake mail sender and a clock the test moves.
/// </summary>
public class PushFailureWatchTests
{
    private const string AccountKey = "DE11222233334444555566";
    private const string ForbiddenToken = "tok-secret-1";
    private const string PayeeName = "NEW SHOP";
    private static readonly ConnectedAccount Account = new("uid-1", AccountKey, "Test Account", "EUR", null);
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedSender(Func<PushMessage, SendResult> respond) : INotificationSender
    {
        public Func<PushMessage, SendResult> Respond { get; set; } = respond;

        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct) => Task.FromResult(Respond(message));
    }

    private sealed class FakeMail : IAlertMailSender
    {
        public List<AlertMail> Mails { get; } = [];

        public Func<AlertMailResult> Respond { get; set; } = AlertMailResult.Ok;

        public bool Throw { get; set; }

        public Task<AlertMailResult> SendAsync(AlertMail mail, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            var result = Respond();
            if (result.Accepted)
            {
                Mails.Add(mail);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class Fetcher(Func<MappedDebits> respond) : IDebitsFetcher
    {
        public Func<MappedDebits> Respond { get; set; } = respond;

        public Task<MappedDebits> FetchAsync(ConnectedAccount account, DateOnly dateFrom, CancellationToken ct) =>
            Task.FromResult(Respond());
    }

    private sealed class StatusChecker : ISessionStatusChecker
    {
        public Task<string?> GetStatusAsync(string sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Factory(string name) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => Ctx(name);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private static GreedyNoseDbContext Ctx(string name) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(name).Options);

    private static EbTransaction Booked(string date, string name, string amount, string entryReference) =>
        new(entryReference, new EbAmount("EUR", amount), new EbParty(name), null, null,
            new EbBankTransactionCode("PMNT", "CCRD", "POSD"), "DBIT", "BOOK", date, null, null, []);

    private sealed record Setup(
        string Db, MutableClock Clock, Fetcher Fetcher, ScriptedSender Sender, FakeMail Mail, IngestionRunner Runner);

    private static async Task<Setup> NewSetupAsync(
        Func<PushMessage, SendResult> respond, bool withToken, DateTimeOffset? consentExpiresAt = null,
        string? sessionHash = null, params EbTransaction[] transactions)
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await using (var ctx = Ctx(db))
        {
            ctx.AccountSyncStates.Add(new AccountSyncState
            {
                UserId = SeedData.UserId,
                AccountKey = AccountKey,
                FirstSyncCompletedAt = Start.AddDays(-30),
                LastSessionIdHash = sessionHash ?? SessionFingerprint.Of("session-1"),
            });
            if (withToken)
            {
                ctx.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = ForbiddenToken, UpdatedAt = Start });
            }

            await ctx.SaveChangesAsync();
        }

        var consent = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await consent.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], Start, consentExpiresAt);

        var fetcher = new Fetcher(() => TransactionMapper.Map(new EbTransactionsResponse(transactions, null), AccountKey));
        var sender = new ScriptedSender(respond);
        var mail = new FakeMail();
        var factory = new Factory(db);
        var watch = new PushFailureWatch(factory, mail, consent, clock, NullLogger<PushFailureWatch>.Instance);
        var runner = new IngestionRunner(
            factory, consent, fetcher, new StatusChecker(), sender, clock, NullLogger<IngestionRunner>.Instance, watch);
        return new Setup(db, clock, fetcher, sender, mail, runner);
    }

    private static Task<Setup> FailingSetupAsync(SendOutcome outcome = SendOutcome.Rejected) =>
        NewSetupAsync(_ => SendResult.Failed(outcome, "scripted"), true, null, null, Booked("2026-10-08", PayeeName, "10.00", "E1"));

    private static async Task TickAsync(Setup s, int hoursAfterStart)
    {
        s.Clock.Now = Start.AddHours(hoursAfterStart);
        await s.Runner.RunOnceAsync(CancellationToken.None);
    }

    private static async Task<PushHealth?> HealthAsync(string db)
    {
        await using var ctx = Ctx(db);
        return await ctx.PushHealth.SingleOrDefaultAsync();
    }

    // Tick 0 sends the first attempt (fails), ticks 1 and 2 retry the mark (fail again): 3 attempts in a row.
    private static async Task ThreeFailuresAsync(Setup s)
    {
        await TickAsync(s, 0);
        await TickAsync(s, 6);
        Assert.Empty(s.Mail.Mails);
        await TickAsync(s, 12);
    }

    [Fact]
    public async Task Three_failed_attempts_in_a_row_send_exactly_one_mail()
    {
        var s = await FailingSetupAsync();

        await ThreeFailuresAsync(s);

        var mail = Assert.Single(s.Mail.Mails);
        Assert.Equal("Greedy Nose: push alerts are failing (Rejected)", mail.Subject);
        Assert.Contains("Test Bank", mail.Text);
        Assert.Contains("Bad debits waiting: 1", mail.Text);
        Assert.Contains("Firebase Admin key", mail.Text);
        Assert.Equal(3, (await HealthAsync(s.Db))!.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_continuing_problem_sends_no_second_mail()
    {
        var s = await FailingSetupAsync();
        await ThreeFailuresAsync(s);

        await TickAsync(s, 18);
        await TickAsync(s, 24 - 1);

        Assert.Single(s.Mail.Mails);
        Assert.True((await HealthAsync(s.Db))!.ConsecutiveFailures >= 5);
    }

    [Fact]
    public async Task Two_failures_and_then_a_success_send_no_mail_and_reset_the_count()
    {
        var s = await FailingSetupAsync();
        await TickAsync(s, 0);
        await TickAsync(s, 6);
        s.Sender.Respond = _ => SendResult.Accepted("m");

        await TickAsync(s, 12);
        s.Sender.Respond = _ => SendResult.Failed(SendOutcome.Rejected, "scripted");
        await TickAsync(s, 13); // nothing owed any more: no attempt, count stays 0

        Assert.Empty(s.Mail.Mails);
        var health = await HealthAsync(s.Db);
        Assert.Equal(0, health!.ConsecutiveFailures);
        Assert.Null(health.FailureMailSentAt);
        Assert.False(health.RecoveryMailPending);
    }

    [Fact]
    public async Task When_pushes_work_again_one_short_followup_is_sent_and_the_incident_is_closed()
    {
        var s = await FailingSetupAsync();
        await ThreeFailuresAsync(s);
        s.Sender.Respond = _ => SendResult.Accepted("m");

        await TickAsync(s, 18);
        await TickAsync(s, 24);

        Assert.Equal(2, s.Mail.Mails.Count);
        var followUp = s.Mail.Mails[1];
        Assert.Equal("Greedy Nose: push alerts work again", followUp.Subject);
        Assert.Contains("sent since then: 1", followUp.Text);
        var health = await HealthAsync(s.Db);
        Assert.Equal(0, health!.ConsecutiveFailures);
        Assert.Null(health.FailureMailSentAt);
        Assert.False(health.RecoveryMailPending);
    }

    [Fact]
    public async Task A_new_incident_after_a_closed_one_sends_a_new_mail()
    {
        var s = await FailingSetupAsync();
        await ThreeFailuresAsync(s);
        s.Sender.Respond = _ => SendResult.Accepted("m");
        await TickAsync(s, 18); // retry succeeds, follow-up
        s.Sender.Respond = _ => SendResult.Failed(SendOutcome.Transient, "scripted");
        s.Fetcher.Respond = () => TransactionMapper.Map(new EbTransactionsResponse(
            [Booked("2026-10-08", PayeeName, "10.00", "E1"), Booked("2026-10-08", "OTHER", "5.00", "E2")], null), AccountKey);

        await TickAsync(s, 24); // new debit, fails (1)
        await TickAsync(s, 30); // 2
        await TickAsync(s, 36); // 3

        Assert.Equal(3, s.Mail.Mails.Count);
        Assert.Equal("Greedy Nose: push alerts are failing (Transient)", s.Mail.Mails[2].Subject);
    }

    [Fact]
    public async Task A_mail_the_sender_refuses_leaves_the_flag_unset_and_is_retried_at_the_next_tick()
    {
        var s = await FailingSetupAsync();
        s.Mail.Respond = () => AlertMailResult.Failed("Brevo answered 401");

        await ThreeFailuresAsync(s);

        Assert.Empty(s.Mail.Mails);
        Assert.Null((await HealthAsync(s.Db))!.FailureMailSentAt);

        s.Mail.Respond = AlertMailResult.Ok;
        await TickAsync(s, 18);

        Assert.Single(s.Mail.Mails);
        Assert.NotNull((await HealthAsync(s.Db))!.FailureMailSentAt);
    }

    [Fact]
    public async Task A_mail_sender_that_throws_does_not_stop_ingestion_or_the_push_retry()
    {
        var s = await FailingSetupAsync();
        s.Mail.Throw = true;
        await TickAsync(s, 0);
        await TickAsync(s, 6);
        s.Fetcher.Respond = () => TransactionMapper.Map(new EbTransactionsResponse(
            [Booked("2026-10-08", PayeeName, "10.00", "E1"), Booked("2026-10-08", "OTHER", "5.00", "E2")], null), AccountKey);

        await TickAsync(s, 12); // third failure: mail attempt throws, tick still ingests the new debit

        await using var ctx = Ctx(s.Db);
        Assert.Equal(2, await ctx.Debits.CountAsync());
        Assert.Null((await HealthAsync(s.Db))!.FailureMailSentAt);

        s.Mail.Throw = false;
        await TickAsync(s, 18);
        Assert.Single(s.Mail.Mails);
    }

    [Fact]
    public async Task The_mail_contains_no_payee_amount_debit_id_token_or_account_number()
    {
        var s = await FailingSetupAsync();
        await ThreeFailuresAsync(s);
        string debitId;
        await using (var ctx = Ctx(s.Db))
        {
            debitId = (await ctx.Debits.SingleAsync()).Id;
        }

        var mail = Assert.Single(s.Mail.Mails);
        var all = mail.Subject + "\n" + mail.Text;
        foreach (var forbidden in new[] { PayeeName, "10.00", "10,00", debitId, ForbiddenToken, AccountKey })
        {
            Assert.DoesNotContain(forbidden, all, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_expiry_push_does_not_count()
    {
        // A consent that has expired: the only push is the R19 expiry push, and it keeps failing.
        var s = await NewSetupAsync(_ => SendResult.Failed(SendOutcome.Rejected, "scripted"), true, Start.AddDays(-1));

        for (var i = 0; i < 5; i++)
        {
            await TickAsync(s, i * 6);
        }

        Assert.Empty(s.Mail.Mails);
        Assert.Null(await HealthAsync(s.Db));
    }

    [Fact]
    public async Task A_failed_reconnect_summary_counts_as_one_attempt()
    {
        var s = await NewSetupAsync(
            _ => SendResult.Failed(SendOutcome.Transient, "scripted"), true, null, "old-session-hash",
            Booked("2026-10-08", PayeeName, "10.00", "E1"));

        await TickAsync(s, 0);

        var health = await HealthAsync(s.Db);
        Assert.Equal(1, health!.ConsecutiveFailures);
        Assert.Equal("Transient", health.LastOutcome);
    }

    [Fact]
    public async Task No_device_token_counts_and_gets_the_open_the_app_line()
    {
        var s = await NewSetupAsync(
            _ => SendResult.Accepted("m"), false, null, null, Booked("2026-10-08", PayeeName, "10.00", "E1"));

        await ThreeFailuresAsync(s);

        var mail = Assert.Single(s.Mail.Mails);
        Assert.Equal("Greedy Nose: push alerts are failing (NoToken)", mail.Subject);
        Assert.Contains("open the app once", mail.Text);
    }

    [Theory]
    [InlineData("Rejected", "Firebase Admin key in Key Vault")]
    [InlineData("Transient", "status page")]
    [InlineData("NoToken", "open the app once")]
    [InlineData("TokenNoLongerValid", "open the app once")]
    [InlineData("Something", "function logs")]
    public void Each_reason_gets_its_check_first_line(string reason, string expected)
    {
        var lines = PushFailureMail.CheckFirstLines(reason);

        Assert.Contains(lines, l => l.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_matching_reason_lines_appear()
    {
        var lines = PushFailureMail.CheckFirstLines("Transient");

        var line = Assert.Single(lines);
        Assert.DoesNotContain("Firebase", line);
    }

    [Fact]
    public void Missing_mail_settings_are_named_without_values()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AlertOptionsValidation.Validate(
            new BrevoOptions { ApiKey = "super-secret-key", SenderEmail = "", SenderName = "x" },
            new AlertOptions { RecipientEmail = "" }));

        Assert.Contains("Brevo:SenderEmail", ex.Message);
        Assert.Contains("Alerts:RecipientEmail", ex.Message);
        Assert.DoesNotContain("Brevo:ApiKey", ex.Message);
        Assert.DoesNotContain("super-secret-key", ex.Message);
    }

    // --- the Brevo sender ---------------------------------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.brevo.com"),
        };
    }

    private static BrevoAlertMailSender Brevo(StubHandler handler) => new(
        new StubFactory(handler),
        new BrevoOptions { ApiKey = "key-123", SenderEmail = "from@example.test", SenderName = "Greedy Nose" },
        new AlertOptions { RecipientEmail = "me@example.test" },
        NullLogger<BrevoAlertMailSender>.Instance);

    [Fact]
    public async Task Brevo_sender_posts_to_the_smtp_endpoint_with_the_api_key_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));

        var result = await Brevo(handler).SendAsync(new AlertMail("subj", "body"), CancellationToken.None);

        Assert.True(result.Accepted);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v3/smtp/email", request.RequestUri!.AbsolutePath);
        Assert.Equal("key-123", Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Contains("\"subject\":\"subj\"", body);
        Assert.Contains("me@example.test", body);
    }

    [Fact]
    public async Task Brevo_sender_turns_an_error_status_into_a_result_without_the_key_or_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("key-123 rejected") });

        var result = await Brevo(handler).SendAsync(new AlertMail("s", "b"), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("401", result.Reason);
        Assert.DoesNotContain("key-123", result.Reason);
    }

    [Fact]
    public async Task Brevo_sender_turns_a_network_exception_into_a_result()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("https://api.brevo.com/secret-path down"));

        var result = await Brevo(handler).SendAsync(new AlertMail("s", "b"), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.DoesNotContain("secret-path", result.Reason);
    }
}
