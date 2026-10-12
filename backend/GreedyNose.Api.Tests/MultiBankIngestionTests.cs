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
/// Story 3.2: one tick walks every bank. Two fake banks (own account, own session, own fetch result), the
/// in-memory database, a fake sender. Covers isolation (NFR6), the per-bank mode, the poll interval, the
/// per-consent attempt record and the shared payee.
/// </summary>
public class MultiBankIngestionTests
{
    private const string KeyA = "DE00000000000000000001";
    private const string KeyB = "DE00000000000000000002";
    private const string BankA = "Bank A";
    private const string BankB = "Bank B";
    private const string Token = "tok-1";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Sender : INotificationSender
    {
        public List<PushMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Accepted("m"));
        }
    }

    /// <summary>One fetch script per account key; a script may throw. Counts the calls per key.</summary>
    private sealed class KeyedFetcher : IDebitsFetcher
    {
        public Dictionary<string, Func<MappedDebits>> Scripts { get; } = [];

        public Dictionary<string, int> Calls { get; } = [];

        public Dictionary<string, Func<CancellationToken, Task<MappedDebits>>> AsyncScripts { get; } = [];

        public List<string> Order { get; } = [];

        public Task<MappedDebits> FetchAsync(ConnectedAccount account, DateOnly dateFrom, CancellationToken ct)
        {
            Calls[account.Key] = Calls.GetValueOrDefault(account.Key) + 1;
            Order.Add(account.Key);
            return AsyncScripts.TryGetValue(account.Key, out var script) ? script(ct) : Task.FromResult(Scripts[account.Key]());
        }
    }

    private sealed class Status(string? answer) : ISessionStatusChecker
    {
        public Task<string?> GetStatusAsync(string sessionId, CancellationToken ct) => Task.FromResult(answer);
    }

    private sealed class ListLogger : ILogger<IngestionRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception.Message));
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

    private static MappedDebits Mapped(string key, params EbTransaction[] transactions) =>
        TransactionMapper.Map(new EbTransactionsResponse(transactions, null), key);

    private static ConnectedAccount Account(string key) => new("uid-" + key, key, "Account", "EUR", null);

    private sealed record Setup(
        string Db, MutableClock Clock, Sender Sender, KeyedFetcher Fetcher, ListLogger Log, ConsentStore Consent, IngestionRunner Runner);

    /// <summary>Both banks connected (sessions sA / sB) with a stored consent row each; nothing else is seeded.</summary>
    private static async Task<Setup> NewSetupAsync(
        string? sessionStatus = null, bool allowShortInterval = false, DateTimeOffset? expiresAtA = null,
        TimeSpan? bankTimeout = null, Func<string, IDbContextFactory<GreedyNoseDbContext>>? factoryFor = null)
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await using (var ctx = Ctx(db))
        {
            ctx.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = Token, UpdatedAt = Start });
            foreach (var bank in new[] { BankA, BankB })
            {
                ctx.BankConsents.Add(new BankConsent
                {
                    UserId = SeedData.UserId,
                    AspspName = bank,
                    SessionIdCipher = [1],
                    PayloadCipher = [1],
                    UpdatedAt = Start,
                });
            }

            await ctx.SaveChangesAsync();
        }

        var consent = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await consent.CompleteAsync(SeedData.UserId, "sA", BankA, [Account(KeyA)], Start, expiresAtA);
        await consent.CompleteAsync(SeedData.UserId, "sB", BankB, [Account(KeyB)], Start.AddMinutes(1), null);

        var sender = new Sender();
        var fetcher = new KeyedFetcher();
        fetcher.Scripts[KeyA] = () => Mapped(KeyA);
        fetcher.Scripts[KeyB] = () => Mapped(KeyB);
        var log = new ListLogger();
        var runner = new IngestionRunner(
            factoryFor?.Invoke(db) ?? new Factory(db), consent, fetcher, new Status(sessionStatus), sender, clock, log,
            options: new IngestionOptions
            {
                AllowShortInterval = allowShortInterval,
                BankTimeout = bankTimeout ?? IngestionOptions.DefaultBankTimeout,
            });
        return new Setup(db, clock, sender, fetcher, log, consent, runner);
    }

    private static async Task SeedSyncAsync(Setup s, string key, string session = "sB")
    {
        await using var ctx = Ctx(s.Db);
        ctx.AccountSyncStates.Add(new AccountSyncState
        {
            UserId = SeedData.UserId,
            AccountKey = key,
            FirstSyncCompletedAt = Start.AddDays(-30),
            LastSessionIdHash = SessionFingerprint.Of(session),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task SetPollAsync(Setup s, string bank, DateTimeOffset? lastAttempt, int? intervalMinutes = null)
    {
        await using var ctx = Ctx(s.Db);
        var row = await ctx.BankConsents.SingleAsync(c => c.AspspName == bank);
        row.LastAttemptAt = lastAttempt;
        row.PollIntervalMinutes = intervalMinutes;
        await ctx.SaveChangesAsync();
    }

    private static async Task<BankConsent> RowAsync(Setup s, string bank)
    {
        await using var ctx = Ctx(s.Db);
        return await ctx.BankConsents.AsNoTracking().SingleAsync(c => c.AspspName == bank);
    }

    // --- Each bank has its own mode ----------------------------------------------------------------

    [Fact]
    public async Task A_first_sync_at_one_bank_and_a_steady_tick_at_the_other_each_run_in_their_own_mode()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () => Mapped(KeyA, Booked("2026-10-10", "OLD SHOP", "5.00", "A1"));
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-12", "NEW SHOP", "7.00", "B1"));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(s.Sender.Sent); // only B's steady debit; A's first sync is silent
        Assert.Contains("7", push.Title);
        await using var verify = Ctx(s.Db);
        Assert.Equal(KeyA, (await verify.Debits.SingleAsync(d => d.Id.Contains("A1"))).AccountKey);
        Assert.Equal(KeyB, (await verify.Debits.SingleAsync(d => d.Id.Contains("B1"))).AccountKey);
        Assert.Equal(2, await verify.AccountSyncStates.CountAsync()); // A's row appeared with its first sync
        Assert.Single(await verify.NotificationLog.ToListAsync());
    }

    [Fact]
    public async Task A_reconnect_at_one_bank_sends_its_summary_while_the_other_banks_first_sync_stays_silent()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "older-session"); // session hash differs from sA: a reconnect
        s.Fetcher.Scripts[KeyA] = () => Mapped(KeyA, Booked("2026-10-12", "NEW SHOP", "7.00", "A1"));
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-11", "OTHER SHOP", "3.00", "B1"));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        var summary = Assert.Single(s.Sender.Sent);
        Assert.Equal(ReconnectSummary.Build(1, 1).Title, summary.Title);
        await using var verify = Ctx(s.Db);
        Assert.Equal(2, await verify.Debits.CountAsync());
        Assert.Equal(2, await verify.AccountSyncStates.CountAsync());
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    // --- Isolation ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_bank_does_not_stop_the_other_and_is_logged_by_name_without_its_message()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () => throw new InvalidOperationException("secret-iban-DE99");
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-12", "NEW SHOP", "7.00", "B1"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Equal("secret-iban-DE99", thrown.Message); // the original exception, as before, after all banks ran
        Assert.Single(s.Sender.Sent); // B was still ingested and pushed
        var line = Assert.Single(s.Log.Lines, l => l.Contains("failed"));
        Assert.Contains(BankA, line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.DoesNotContain(s.Log.Lines, l => l.Contains("secret-iban"));

        var a = await RowAsync(s, BankA);
        Assert.Equal(Start, a.LastAttemptAt);
        Assert.Null(a.LastSuccessAt);
        Assert.Equal(nameof(InvalidOperationException), a.LastError);
        var b = await RowAsync(s, BankB);
        Assert.Equal(Start, b.LastSuccessAt);
        Assert.Null(b.LastError);
    }

    [Fact]
    public async Task A_rate_limit_at_one_bank_is_recorded_with_its_code_and_the_other_bank_goes_on()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () => throw new EnableBankingRequestException(429, "ASPSP_RATE_LIMIT_EXCEEDED", "wait");
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-12", "NEW SHOP", "7.00", "B1"));

        await Assert.ThrowsAsync<EnableBankingRequestException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Single(s.Sender.Sent);
        Assert.Equal("EnableBankingRequestException 429 ASPSP_RATE_LIMIT_EXCEEDED", (await RowAsync(s, BankA)).LastError);
    }

    [Fact]
    public async Task When_both_banks_fail_every_failure_is_rethrown_together_after_both_were_tried()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () => throw new InvalidOperationException("a");
        s.Fetcher.Scripts[KeyB] = () => throw new TimeoutException("b");

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Equal(1, s.Fetcher.Calls[KeyA]);
        Assert.Equal(1, s.Fetcher.Calls[KeyB]);
        Assert.Equal(nameof(InvalidOperationException), (await RowAsync(s, BankA)).LastError);
        Assert.Equal(nameof(TimeoutException), (await RowAsync(s, BankB)).LastError);
    }

    [Fact]
    public async Task A_dead_consent_at_one_bank_pushes_its_expiry_notice_and_the_other_bank_is_still_ingested()
    {
        var s = await NewSetupAsync(sessionStatus: "CLOSED");
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () => throw new EnableBankingRequestException(401, "CLOSED_SESSION");
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-12", "NEW SHOP", "7.00", "B1"));

        await s.Runner.RunOnceAsync(CancellationToken.None); // a dead consent is handled, not a failure

        Assert.Equal(2, s.Sender.Sent.Count);
        Assert.Contains(s.Sender.Sent, m => m.Title == ConnectionExpiredNotice.Title);
        Assert.Equal("ConsentClosedByBank", (await RowAsync(s, BankA)).LastError);
        Assert.Null((await RowAsync(s, BankB)).LastError);
    }

    [Fact]
    public async Task A_first_sync_that_fails_leaves_no_rows_while_the_other_banks_first_sync_is_committed()
    {
        var s = await NewSetupAsync();
        s.Fetcher.Scripts[KeyA] = () => throw new InvalidOperationException("down");
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-10", "SHOP", "5.00", "B1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        await using var verify = Ctx(s.Db);
        Assert.Equal(KeyB, (await verify.AccountSyncStates.SingleAsync()).AccountKey);
        Assert.All(await verify.Debits.ToListAsync(), d => Assert.Equal(KeyB, d.AccountKey));
        Assert.Single(await verify.Debits.ToListAsync());
        Assert.Empty(s.Sender.Sent);
    }

    // --- Interval -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_bank_polled_an_hour_ago_is_skipped_and_its_record_is_untouched()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddHours(-1));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.False(s.Fetcher.Calls.ContainsKey(KeyA));
        Assert.Equal(1, s.Fetcher.Calls[KeyB]); // B was never polled: polled now
        Assert.Equal(Start.AddHours(-1), (await RowAsync(s, BankA)).LastAttemptAt);
    }

    [Theory]
    [InlineData(5, false)] // 5 h after the last attempt, 6 h default: skipped
    [InlineData(6, true)]
    [InlineData(7, true)]
    public async Task The_default_interval_is_six_hours(int hoursAgo, bool polled)
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddHours(-hoursAgo), null);

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(polled, s.Fetcher.Calls.ContainsKey(KeyA));
    }

    [Fact]
    public async Task A_timer_that_fires_a_few_seconds_early_does_not_skip_the_bank()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddHours(-6).AddSeconds(20));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.True(s.Fetcher.Calls.ContainsKey(KeyA));
    }

    [Theory]
    [InlineData(60, 2, true)] // own interval 1 h: due after 2 h
    [InlineData(720, 7, false)] // own interval 12 h: not due after 7 h
    public async Task A_per_bank_interval_is_honoured(int intervalMinutes, int hoursAgo, bool polled)
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddHours(-hoursAgo), intervalMinutes);

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(polled, s.Fetcher.Calls.ContainsKey(KeyA));
    }

    [Fact]
    public async Task A_first_sync_that_is_not_done_is_never_skipped()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyB, "sB"); // A has no sync row
        await SetPollAsync(s, BankA, Start.AddMinutes(-10));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.True(s.Fetcher.Calls.ContainsKey(KeyA));
        await using var verify = Ctx(s.Db);
        Assert.True(await verify.AccountSyncStates.AnyAsync(r => r.AccountKey == KeyA));
    }

    [Fact]
    public async Task A_reconnect_is_never_skipped()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "older-session");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddMinutes(-10));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.True(s.Fetcher.Calls.ContainsKey(KeyA));
    }

    [Fact]
    public async Task The_short_interval_switch_turns_the_skip_off()
    {
        var s = await NewSetupAsync(allowShortInterval: true);
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddMinutes(-1));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.True(s.Fetcher.Calls.ContainsKey(KeyA));
    }

    [Fact]
    public async Task An_expired_consent_still_gets_its_expiry_push_inside_the_interval()
    {
        var s = await NewSetupAsync(expiresAtA: Start.AddDays(-1));
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddMinutes(-10));

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.Contains(s.Sender.Sent, m => m.Title == ConnectionExpiredNotice.Title);
        Assert.False(s.Fetcher.Calls.ContainsKey(KeyA));
        Assert.Equal("ConsentExpired", (await RowAsync(s, BankA)).LastError);
    }

    [Fact]
    public async Task A_successful_tick_records_the_attempt_and_clears_an_earlier_error()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await using (var ctx = Ctx(s.Db))
        {
            (await ctx.BankConsents.SingleAsync(c => c.AspspName == BankA)).LastError = "Old";
            await ctx.SaveChangesAsync();
        }

        await s.Runner.RunOnceAsync(CancellationToken.None);

        var a = await RowAsync(s, BankA);
        Assert.Equal(Start, a.LastAttemptAt);
        Assert.Equal(Start, a.LastSuccessAt);
        Assert.Null(a.LastError);
    }

    // --- Shared payee ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_merchant_at_two_banks_is_one_payee_and_one_rule_and_each_debit_keeps_its_account()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        var shopA = Mapped(KeyA, Booked("2026-10-12", "SHARED SHOP", "7.00", "A1"));
        var shopB = Mapped(KeyB, Booked("2026-10-12", "SHARED SHOP", "9.00", "B1"));
        var payeeId = Assert.Single(shopA.Payload.Payees).Id;
        Assert.Equal(payeeId, Assert.Single(shopB.Payload.Payees).Id);
        s.Fetcher.Scripts[KeyA] = () => shopA;
        s.Fetcher.Scripts[KeyB] = () => shopB;
        await using (var ctx = Ctx(s.Db))
        {
            ctx.Payees.Add(new Payee { UserId = SeedData.UserId, Id = payeeId, Name = "Shared Shop", Initials = "SS", FirstSeenAt = Start });
            ctx.Rules.Add(new Rule
            {
                UserId = SeedData.UserId, PayeeId = payeeId, Classification = Classification.Good, AmountEUR = 8m, UpdatedAt = Start,
            });
            await ctx.SaveChangesAsync();
        }

        await s.Runner.RunOnceAsync(CancellationToken.None);

        await using var verify = Ctx(s.Db);
        Assert.Single(await verify.Payees.ToListAsync());
        Assert.Single(await verify.Rules.ToListAsync());
        var debits = await verify.Debits.OrderBy(d => d.AccountKey).ToListAsync();
        Assert.Equal([KeyA, KeyB], debits.Select(d => d.AccountKey).ToArray());
        Assert.All(debits, d => Assert.Equal(payeeId, d.PayeeId));
        // The one rule (limit 8) judges each bank's debit: 7 passes, 9 is over the limit and alerts.
        var push = Assert.Single(s.Sender.Sent);
        Assert.Contains("9", push.Title);
    }

    // --- Time budget, order, attempt stamp, mid-commit failure ----------------------------------------

    [Fact]
    public async Task A_hanging_bank_is_cut_off_at_its_own_budget_recorded_and_the_next_bank_still_runs()
    {
        var s = await NewSetupAsync(bankTimeout: TimeSpan.FromMilliseconds(200));
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.AsyncScripts[KeyA] = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Mapped(KeyA);
        };
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-12", "NEW SHOP", "7.00", "B1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Single(s.Sender.Sent);
        var a = await RowAsync(s, BankA);
        Assert.Equal("Timeout", a.LastError);
        Assert.Equal(Start, a.LastAttemptAt);
        Assert.Equal(Start, (await RowAsync(s, BankB)).LastSuccessAt);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_stops_the_walk()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Runner.RunOnceAsync(cts.Token));

        Assert.Empty(s.Fetcher.Calls);
    }

    [Fact]
    public async Task The_bank_tried_longest_ago_goes_first_and_a_never_tried_bank_before_all()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        await SetPollAsync(s, BankA, Start.AddHours(-7)); // A connected first, but B was never tried

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal([KeyB, KeyA], s.Fetcher.Order.ToArray());
    }

    [Fact]
    public async Task The_attempt_is_stamped_with_the_start_of_the_tick_not_the_end_of_the_banks_turn()
    {
        var s = await NewSetupAsync();
        await SeedSyncAsync(s, KeyA, "sA");
        await SeedSyncAsync(s, KeyB, "sB");
        s.Fetcher.Scripts[KeyA] = () =>
        {
            s.Clock.Now = Start.AddMinutes(30); // a slow tick
            return Mapped(KeyA);
        };

        await s.Runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(Start, (await RowAsync(s, BankA)).LastAttemptAt);
        Assert.Equal(Start, (await RowAsync(s, BankA)).LastSuccessAt);
    }

    /// <summary>Fails the one save that would write bank A's first-sync marker, as a database dying mid-commit would.</summary>
    private sealed class FailFirstSyncSaveInterceptor(Func<bool> active) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (active() && eventData.Context!.ChangeTracker.Entries<AccountSyncState>()
                    .Any(e => e.State == EntityState.Added && e.Entity.AccountKey == KeyA))
            {
                throw new InvalidOperationException("db died mid-commit");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InterceptedFactory(string name, Func<bool> active) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<GreedyNoseDbContext>()
                .UseInMemoryDatabase(name)
                .AddInterceptors(new FailFirstSyncSaveInterceptor(active))
                .Options);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task A_first_sync_whose_commit_fails_leaves_nothing_of_it_the_other_bank_commits_and_the_next_tick_retries_it()
    {
        var failing = true;
        var s = await NewSetupAsync(factoryFor: db => new InterceptedFactory(db, () => failing));
        s.Fetcher.Scripts[KeyA] = () => Mapped(KeyA, Booked("2026-10-10", "SHOP A", "5.00", "A1"), Booked("2026-10-11", "SHOP A", "6.00", "A2"));
        s.Fetcher.Scripts[KeyB] = () => Mapped(KeyB, Booked("2026-10-10", "SHOP B", "5.00", "B1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Runner.RunOnceAsync(CancellationToken.None));

        await using (var verify = Ctx(s.Db))
        {
            Assert.DoesNotContain(await verify.Debits.ToListAsync(), d => d.AccountKey == KeyA);
            Assert.DoesNotContain(await verify.AccountSyncStates.ToListAsync(), r => r.AccountKey == KeyA);
            Assert.Single(await verify.Debits.Where(d => d.AccountKey == KeyB).ToListAsync());
            Assert.Contains(await verify.AccountSyncStates.ToListAsync(), r => r.AccountKey == KeyB);
        }

        failing = false;
        s.Clock.Now = Start.AddMinutes(10); // inside the interval: a first sync that is not done is never skipped
        await s.Runner.RunOnceAsync(CancellationToken.None);

        await using var after = Ctx(s.Db);
        Assert.Equal(2, await after.Debits.CountAsync(d => d.AccountKey == KeyA));
        Assert.Contains(await after.AccountSyncStates.ToListAsync(), r => r.AccountKey == KeyA);
        Assert.Empty(s.Sender.Sent); // still the silent first sync
    }
}
