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
/// R11a (Story 2.9): a failed push is remembered with an explicit mark and retried on every later tick for
/// 24 h. Same in-memory plumbing as <c>IngestionRunnerTests</c> (which keeps its helpers private), with a
/// sender whose answers are scripted per message and a clock the test moves. The Postgres-only unique
/// violation catch in the retry stage is, as there, not provable against the in-memory provider.
/// </summary>
public class PendingPushTests
{
    private const string AccountKey = "DE11222233334444555566";
    private static readonly ConnectedAccount Account = new("uid-1", AccountKey, "Test Account", "EUR", null);
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedSender(Func<PushMessage, SendResult> respond) : INotificationSender
    {
        public List<PushMessage> Sent { get; } = [];

        public Func<PushMessage, SendResult> Respond { get; set; } = respond;

        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(Respond(message));
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Factory(string name) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => Ctx(name);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private static GreedyNoseDbContext Ctx(string name) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(name).Options);

    private static ConsentStore Consent(TimeProvider clock, bool connected = true, DateTimeOffset? expiresAt = null)
    {
        var store = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        if (connected)
        {
            store.CompleteAsync("session-1", "Test Bank", [Account], clock.GetUtcNow(), expiresAt).GetAwaiter().GetResult();
        }

        return store;
    }

    private static EbTransaction Booked(string date, string name, string amount, string entryReference) =>
        new(entryReference, new EbAmount("EUR", amount), new EbParty(name), null, null,
            new EbBankTransactionCode("PMNT", "CCRD", "POSD"), "DBIT", "BOOK", date, null, null, []);

    private static MappedDebits Mapped(params EbTransaction[] transactions) =>
        TransactionMapper.Map(new EbTransactionsResponse(transactions, null), AccountKey);

    private static IngestionRunner Runner(
        string db, ConsentStore consent, IDebitsFetcher fetcher, INotificationSender sender, TimeProvider clock, ILogger<IngestionRunner>? logger = null) =>
        new(new Factory(db), consent, fetcher, new StatusChecker(), sender, clock, logger ?? NullLogger<IngestionRunner>.Instance);

    private static async Task SeedSteadyAsync(string db, TimeProvider clock, string? sessionHash = null)
    {
        await using var ctx = Ctx(db);
        ctx.AccountSyncStates.Add(new AccountSyncState
        {
            UserId = SeedData.UserId,
            AccountKey = AccountKey,
            FirstSyncCompletedAt = clock.GetUtcNow().AddDays(-30),
            LastSessionIdHash = sessionHash ?? SessionFingerprint.Of("session-1"),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task AddTokenAsync(string db, string token, TimeProvider clock)
    {
        await using var ctx = Ctx(db);
        ctx.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = token, UpdatedAt = clock.GetUtcNow() });
        await ctx.SaveChangesAsync();
    }

    private static SendResult Ok(PushMessage _) => SendResult.Accepted("m");

    private static SendResult Fail(SendOutcome outcome) => SendResult.Failed(outcome, "scripted");

    private static async Task<List<PendingPush>> MarksAsync(string db)
    {
        await using var ctx = Ctx(db);
        return await ctx.PendingPushes.OrderBy(p => p.CreatedAt).ToListAsync();
    }

    // The standard setup: steady state, charges from unreviewed (so bad) payees.
    private sealed record Setup(string Db, MutableClock Clock, ConsentStore Consent, Fetcher Fetcher, ScriptedSender Sender, IngestionRunner Runner);

    private static async Task<Setup> NewSetupAsync(Func<PushMessage, SendResult> respond, bool withToken, params EbTransaction[] transactions)
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await SeedSteadyAsync(db, clock);
        if (withToken)
        {
            await AddTokenAsync(db, "tok-1", clock);
        }

        var consent = Consent(clock);
        var fetcher = new Fetcher(() => Mapped(transactions));
        var sender = new ScriptedSender(respond);
        return new Setup(db, clock, consent, fetcher, sender, Runner(db, consent, fetcher, sender, clock));
    }

    private static Task Tick(Setup s) => s.Runner.RunOnceAsync(CancellationToken.None);

    // --- first attempt ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_first_push_stores_the_debit_and_an_open_mark_with_the_frozen_reason()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Rejected), true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));

        await Tick(s);

        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Equal(PendingPushKind.Debit, mark.Kind);
        Assert.Equal(1, mark.Attempts);
        Assert.Equal("Rejected", mark.LastOutcome);
        Assert.Equal(Start, mark.CreatedAt);
        Assert.Null(mark.EndedAt);
        Assert.False(string.IsNullOrWhiteSpace(mark.Reason));
        await using var verify = Ctx(s.Db);
        Assert.Equal(mark.DebitId, (await verify.Debits.SingleAsync()).Id);
        Assert.Empty(await verify.NotificationLog.ToListAsync());
        Assert.Single(s.Sender.Sent); // the mark was created by this tick, so it is not retried in it
    }

    [Fact]
    public async Task With_no_device_token_the_debit_is_marked_NoToken()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));

        await Tick(s);

        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Equal("NoToken", mark.LastOutcome);
        Assert.Equal(1, mark.Attempts);
        Assert.Empty(s.Sender.Sent);
    }

    [Fact]
    public async Task A_sent_push_creates_no_mark()
    {
        var s = await NewSetupAsync(Ok, true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));

        await Tick(s);

        Assert.Empty(await MarksAsync(s.Db));
    }

    // --- retry --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_next_tick_sends_the_owed_push_once_logs_it_ends_the_mark_and_never_sends_it_again()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s); // no token: owed
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        s.Clock.Now = Start.AddHours(6);
        await Tick(s);
        s.Clock.Now = Start.AddHours(12);
        await Tick(s);

        var push = Assert.Single(s.Sender.Sent);
        Assert.StartsWith("NEW SHOP", push.Title);
        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Equal(PendingPush.EndedSent, mark.EndReason);
        Assert.Equal(Start.AddHours(6), mark.EndedAt);
        Assert.Equal(2, mark.Attempts);
        Assert.Equal("Sent", mark.LastOutcome);
        await using var verify = Ctx(s.Db);
        var log = Assert.Single(await verify.NotificationLog.ToListAsync());
        Assert.Equal(mark.DebitId, log.DebitId);
        Assert.Equal(mark.Reason, log.Reason);
    }

    [Fact]
    public async Task A_failing_retry_keeps_the_mark_open_and_records_attempts_outcome_and_time()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Transient), true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);

        s.Clock.Now = Start.AddHours(6);
        await Tick(s);
        s.Sender.Respond = _ => Fail(SendOutcome.Rejected);
        s.Clock.Now = Start.AddHours(12);
        await Tick(s);

        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Null(mark.EndedAt);
        Assert.Equal(3, mark.Attempts);
        Assert.Equal("Rejected", mark.LastOutcome);
        Assert.Equal(Start.AddHours(12), mark.LastAttemptAt);
    }

    [Fact]
    public async Task After_24_hours_the_push_is_given_up_the_debit_stays_and_nothing_is_sent_again()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Transient), true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);

        s.Clock.Now = Start.AddHours(24); // exactly 24 h: still owed
        await Tick(s);
        Assert.Null(Assert.Single(await MarksAsync(s.Db)).EndedAt);

        s.Clock.Now = Start.AddHours(24).AddMinutes(1);
        await Tick(s);
        var sentAtGiveUp = s.Sender.Sent.Count;
        s.Clock.Now = Start.AddHours(30);
        await Tick(s);

        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Equal(PendingPush.EndedGaveUp, mark.EndReason);
        Assert.NotNull(mark.EndedAt);
        Assert.Equal(sentAtGiveUp, s.Sender.Sent.Count);
        await using var verify = Ctx(s.Db);
        Assert.Equal(1, await verify.Debits.CountAsync());
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    [Fact]
    public async Task A_debit_that_turns_bad_later_through_a_rule_edit_gets_no_mark_and_no_push()
    {
        var s = await NewSetupAsync(Ok, true, Booked("2026-09-22", "LATER SHOP", "10.00", "E1"));
        var payeeId = "name:LATER SHOP";
        await using (var ctx = Ctx(s.Db))
        {
            ctx.Payees.Add(new Payee { UserId = SeedData.UserId, Id = payeeId, Name = "LATER SHOP", Initials = "LS", FirstSeenAt = Start });
            ctx.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = payeeId, Classification = Classification.Good, AmountEUR = null, UpdatedAt = Start });
            await ctx.SaveChangesAsync();
        }

        await Tick(s); // good: stored silently
        await using (var ctx = Ctx(s.Db))
        {
            (await ctx.Rules.SingleAsync()).Classification = Classification.Bad;
            await ctx.SaveChangesAsync();
        }

        s.Clock.Now = Start.AddHours(6);
        await Tick(s);

        Assert.Empty(s.Sender.Sent);
        Assert.Empty(await MarksAsync(s.Db));
    }

    [Fact]
    public async Task A_failing_retry_does_not_stop_ingestion_of_new_debits()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Transient), true, Booked("2026-09-22", "OLD SHOP", "10.00", "E1"));
        await Tick(s);

        s.Fetcher.Respond = () => Mapped(Booked("2026-09-22", "OLD SHOP", "10.00", "E1"), Booked("2026-09-23", "FRESH SHOP", "5.00", "E2"));
        s.Sender.Respond = m => m.Title.StartsWith("FRESH") ? Ok(m) : Fail(SendOutcome.Transient);
        s.Clock.Now = Start.AddHours(6);
        await Tick(s);

        await using var verify = Ctx(s.Db);
        Assert.Equal(2, await verify.Debits.CountAsync());
        var logged = Assert.Single(await verify.NotificationLog.ToListAsync());
        var open = Assert.Single(await MarksAsync(s.Db), m => m.EndedAt is null);
        Assert.NotEqual(logged.DebitId, open.DebitId);
    }

    [Fact]
    public async Task A_throwing_retry_of_one_item_does_not_block_the_next_one()
    {
        var s = await NewSetupAsync(
            _ => Fail(SendOutcome.Transient), true,
            Booked("2026-09-21", "FIRST SHOP", "10.00", "E1"), Booked("2026-09-22", "SECOND SHOP", "10.00", "E2"));
        await Tick(s);
        Assert.Equal(2, (await MarksAsync(s.Db)).Count);

        s.Sender.Respond = m => m.Title.StartsWith("FIRST") ? throw new InvalidOperationException("boom") : Ok(m);
        s.Clock.Now = Start.AddHours(6);
        await Tick(s); // must not throw

        var marks = await MarksAsync(s.Db);
        Assert.Single(marks, m => m.EndedAt is not null && m.EndReason == PendingPush.EndedSent);
        Assert.Single(marks, m => m.EndedAt is null);
    }

    [Fact]
    public async Task Owed_pushes_are_retried_oldest_first()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Transient), true, Booked("2026-09-21", "FIRST SHOP", "10.00", "E1"));
        await Tick(s);
        s.Fetcher.Respond = () => Mapped(Booked("2026-09-21", "FIRST SHOP", "10.00", "E1"), Booked("2026-09-22", "SECOND SHOP", "10.00", "E2"));
        s.Clock.Now = Start.AddHours(1);
        await Tick(s); // retries FIRST, then ingests and marks SECOND
        s.Sender.Sent.Clear();

        s.Clock.Now = Start.AddHours(2);
        await Tick(s);

        Assert.Equal(2, s.Sender.Sent.Count);
        Assert.StartsWith("FIRST", s.Sender.Sent[0].Title);
        Assert.StartsWith("SECOND", s.Sender.Sent[1].Title);
    }

    [Fact]
    public async Task Retries_still_run_when_there_is_no_consent()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s); // owed: no token
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        var noConsent = Runner(s.Db, Consent(s.Clock, connected: false), s.Fetcher, s.Sender, s.Clock);
        s.Clock.Now = Start.AddHours(6);
        await noConsent.RunOnceAsync(CancellationToken.None);

        Assert.Single(s.Sender.Sent);
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
    }

    [Fact]
    public async Task Retries_still_run_when_the_consent_is_expired()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        s.Clock.Now = Start.AddHours(6);
        var expired = Runner(s.Db, Consent(s.Clock, expiresAt: s.Clock.Now), s.Fetcher, s.Sender, s.Clock);
        await expired.RunOnceAsync(CancellationToken.None);

        // The expiry push (R19) and the owed push both go out; the mark is ended.
        Assert.Contains(s.Sender.Sent, m => m.Title.StartsWith("NEW SHOP"));
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
    }

    [Fact]
    public async Task Retries_still_run_when_the_fetch_throws_and_the_exception_still_propagates()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        s.Fetcher.Respond = () => throw new HttpRequestException("bank down");
        s.Clock.Now = Start.AddHours(6);
        await Assert.ThrowsAsync<HttpRequestException>(() => Tick(s));

        Assert.Single(s.Sender.Sent);
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
    }

    [Fact]
    public async Task Retries_still_send_when_a_slow_fetch_has_used_up_the_tick_token()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        var slow = new WaitingFetcher();
        var runner = Runner(s.Db, s.Consent, slow, s.Sender, s.Clock);
        using var tick = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        s.Clock.Now = Start.AddHours(6);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(tick.Token));

        Assert.Single(s.Sender.Sent);
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
    }

    [Fact]
    public async Task Retries_still_send_when_the_tick_token_is_already_cancelled()
    {
        var s = await NewSetupAsync(Ok, false, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);
        await AddTokenAsync(s.Db, "tok-1", s.Clock);

        using var tick = new CancellationTokenSource();
        await tick.CancelAsync();
        s.Clock.Now = Start.AddHours(6);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Runner.RunOnceAsync(tick.Token));

        Assert.Single(s.Sender.Sent);
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
    }

    [Fact]
    public async Task A_failing_read_of_the_owed_pushes_does_not_stop_ingestion()
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await SeedSteadyAsync(db, clock);
        await AddTokenAsync(db, "tok-1", clock);
        var fetcher = new Fetcher(() => Mapped(Booked("2026-09-22", "NEW SHOP", "10.00", "E1")));
        var sender = new ScriptedSender(Ok);
        var runner = new IngestionRunner(
            new FailFirstFactory(db), Consent(clock), fetcher, new StatusChecker(), sender, clock, NullLogger<IngestionRunner>.Instance);

        await runner.RunOnceAsync(CancellationToken.None);

        await using var verify = Ctx(db);
        Assert.Equal(1, await verify.Debits.CountAsync());
        Assert.Single(await verify.NotificationLog.ToListAsync());
    }

    private sealed class WaitingFetcher : IDebitsFetcher
    {
        public async Task<MappedDebits> FetchAsync(ConnectedAccount account, DateOnly dateFrom, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    // The first context request of a tick is the owed-ids read.
    private sealed class FailFirstFactory(string name) : IDbContextFactory<GreedyNoseDbContext>
    {
        private int calls;

        public GreedyNoseDbContext CreateDbContext() =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("db hiccup") : Ctx(name);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    // --- pruning --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TokenNoLongerValid_still_prunes_but_the_push_stays_owed()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.TokenNoLongerValid), true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));

        await Tick(s);

        await using var verify = Ctx(s.Db);
        Assert.Empty(await verify.DeviceTokens.ToListAsync());
        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Null(mark.EndedAt);
        Assert.Equal("TokenNoLongerValid", mark.LastOutcome);
    }

    [Fact]
    public async Task Rejected_never_prunes_and_keeps_the_push_owed_across_retries()
    {
        var s = await NewSetupAsync(_ => Fail(SendOutcome.Rejected), true, Booked("2026-09-22", "NEW SHOP", "10.00", "E1"));
        await Tick(s);
        s.Clock.Now = Start.AddHours(6);
        await Tick(s);

        await using var verify = Ctx(s.Db);
        Assert.Single(await verify.DeviceTokens.ToListAsync());
        Assert.Null(Assert.Single(await MarksAsync(s.Db)).EndedAt);
    }

    // --- reconnect summary ------------------------------------------------------------------------------------

    private static async Task<Setup> NewReconnectSetupAsync(bool withToken)
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await SeedSteadyAsync(db, clock, SessionFingerprint.Of("old-session"));
        if (withToken)
        {
            await AddTokenAsync(db, "tok-1", clock);
        }

        var consent = Consent(clock);
        var fetcher = new Fetcher(() => Mapped(
            Booked("2026-09-20", "SHOP A", "10.00", "R1"), Booked("2026-09-21", "SHOP B", "11.00", "R2")));
        var sender = new ScriptedSender(_ => Fail(SendOutcome.Transient));
        return new Setup(db, clock, consent, fetcher, sender, Runner(db, consent, fetcher, sender, clock));
    }

    [Fact]
    public async Task A_failed_reconnect_summary_is_remembered_with_frozen_counts_and_the_debits_still_commit()
    {
        var s = await NewReconnectSetupAsync(withToken: true);

        await Tick(s);

        var mark = Assert.Single(await MarksAsync(s.Db));
        Assert.Equal(PendingPushKind.ReconnectSummary, mark.Kind);
        Assert.Null(mark.DebitId);
        Assert.Equal(2, mark.SummaryNewCount);
        Assert.Equal(2, mark.SummaryBadCount);
        Assert.Equal("Transient", mark.LastOutcome);
        await using var verify = Ctx(s.Db);
        Assert.Equal(2, await verify.Debits.CountAsync());
        Assert.Equal(SessionFingerprint.Of("session-1"), (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
    }

    [Fact]
    public async Task A_reconnect_summary_with_no_token_is_retried_until_sent_and_never_sent_twice()
    {
        var s = await NewReconnectSetupAsync(withToken: false);
        await Tick(s);
        Assert.Equal("NoToken", Assert.Single(await MarksAsync(s.Db)).LastOutcome);
        await AddTokenAsync(s.Db, "tok-1", s.Clock);
        s.Sender.Respond = Ok;

        s.Clock.Now = Start.AddHours(6);
        await Tick(s); // steady now; retries the summary
        s.Clock.Now = Start.AddHours(12);
        await Tick(s);

        var push = Assert.Single(s.Sender.Sent);
        Assert.Equal("2 new debits while you were disconnected", push.Title);
        Assert.Equal("2 of them are bad. Tap to review them.", push.Body);
        Assert.Equal(PendingPush.EndedSent, Assert.Single(await MarksAsync(s.Db)).EndReason);
        await using var verify = Ctx(s.Db);
        Assert.Empty(await verify.NotificationLog.ToListAsync()); // a summary belongs to no debit
    }

    [Fact]
    public async Task A_reconnect_summary_is_given_up_after_24_hours()
    {
        var s = await NewReconnectSetupAsync(withToken: true);
        await Tick(s);

        s.Clock.Now = Start.AddHours(25);
        await Tick(s);
        var sentAtGiveUp = s.Sender.Sent.Count;
        s.Clock.Now = Start.AddHours(31);
        await Tick(s);

        Assert.Equal(PendingPush.EndedGaveUp, Assert.Single(await MarksAsync(s.Db)).EndReason);
        Assert.Equal(sentAtGiveUp, s.Sender.Sent.Count);
    }

    // --- logging ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logs_of_the_failure_and_the_retry_carry_no_payee_name_and_no_amount()
    {
        var log = new CapturingLogger<IngestionRunner>();
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await SeedSteadyAsync(db, clock);
        await AddTokenAsync(db, "tok-secret-1", clock);
        var fetcher = new Fetcher(() => Mapped(Booked("2026-09-22", "Secret Shop", "73.41", "E1")));
        var sender = new ScriptedSender(_ => Fail(SendOutcome.Transient));
        var runner = Runner(db, Consent(clock), fetcher, sender, clock, log);

        await runner.RunOnceAsync(CancellationToken.None);
        sender.Respond = Ok;
        clock.Now = Start.AddHours(6);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.Contains("attempt 2") && m.Contains("Sent"));
        Assert.All(log.Messages, m =>
        {
            Assert.DoesNotContain("secret shop", m, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("73", m);
            Assert.DoesNotContain("tok-secret-1", m);
        });
    }
}
