using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// One poll tick (NOTIFICATION-TRACER-BULLET.md, step 6), against EF Core's in-memory provider and
/// a fake fetcher/sender — never real Postgres, never real HTTP. Mirrors RulesTests.cs's shape.
///
/// Not covered here, for the same reason <c>DeviceTokenTests.cs</c> names explicitly: the two race
/// catches in <c>IngestionRunner</c> (the payee-upsert race on <c>PK_Payees</c>, and the
/// notification-log race on <c>IX_NotificationLog_UserId_DebitId</c>) both pattern-match on a real
/// <c>PostgresException</c>, which the in-memory provider cannot raise — it throws a plain
/// <c>ArgumentException</c> on a duplicate key instead, so a test against it could not tell a
/// working catch from a silently-never-matching one. Proving either catch is what the project's
/// no-database rule leaves to a run against real Postgres.
/// </summary>
public class IngestionRunnerTests
{
    private const string AccountKey = "DE11222233334444555566";
    private static readonly ConnectedAccount Account = new("uid-1", AccountKey, "Test Account", "EUR", null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ConsentStore NewConsentStore(TimeProvider clock, bool connected = true, ConnectedAccount? account = null, string sessionId = "session-1", DateTimeOffset? expiresAt = null)
    {
        // ConsentStore has no test seam — it is a concrete sealed class with real file I/O in
        // Save(). A temp path keeps tests off the repo's real consent.local.json. Complete() sets
        // Accounts directly (see ConsentStore.cs), so no StartAuthorization/IsExpectedState dance
        // is needed to get a "connected" store for a test.
        var store = new ConsentStore(
            Path.Combine(Path.GetTempPath(), $"ingestion-test-consent-{Guid.NewGuid():N}.json"),
            TimeSpan.FromDays(90),
            NullLogger<ConsentStore>.Instance);

        if (connected)
        {
            store.Complete(sessionId, [account ?? Account], clock.GetUtcNow(), expiresAt);
        }

        return store;
    }

    private sealed class FakeDebitsFetcher(MappedDebits result) : IDebitsFetcher
    {
        public int CallCount { get; private set; }

        public Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSender(Func<string, SendResult> respond) : INotificationSender
    {
        public List<PushMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(respond(message.Token));
        }
    }

    private static GreedyNoseDbContext NewInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

    // A minimal IDbContextFactory over UseInMemoryDatabase, since AddDbContextFactory needs a real
    // host to resolve — tests build the plumbing by hand instead, same database name every call so
    // every context sees the same in-memory store.
    private sealed class InMemoryDbContextFactory(string databaseName) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => NewInMemoryContext(databaseName);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>One card-payment debit for one payee, shaped like TransactionMapperTests' fixtures.</summary>
    private static EbTransaction Booked(string date, string name, string amount = "10.00", string? entryReference = null) =>
        new(
            EntryReference: entryReference,
            TransactionAmount: new EbAmount("EUR", amount),
            Creditor: new EbParty(name),
            CreditorAccount: null,
            CreditorAgent: null,
            BankTransactionCode: new EbBankTransactionCode("PMNT", "CCRD", "POSD"),
            CreditDebitIndicator: "DBIT",
            Status: "BOOK",
            BookingDate: date,
            ValueDate: null,
            TransactionDate: null,
            RemittanceInformation: []);

    private static MappedDebits Mapped(params EbTransaction[] transactions) => MappedFor(AccountKey, transactions);

    private static MappedDebits MappedFor(string accountKey, params EbTransaction[] transactions) =>
        TransactionMapper.Map(new EbTransactionsResponse(transactions, null), accountKey);

    private static IngestionRunner NewRunner(
        IDbContextFactory<GreedyNoseDbContext> dbFactory, ConsentStore consent, IDebitsFetcher fetcher,
        INotificationSender sender, TimeProvider clock, ISessionStatusChecker? checker = null) =>
        new(dbFactory, consent, fetcher, checker ?? new FakeSessionStatusChecker(() => null), sender, clock, NullLogger<IngestionRunner>.Instance);

    private sealed class FakeSessionStatusChecker(Func<string?> respond) : ISessionStatusChecker
    {
        public int CallCount { get; private set; }

        public Task<string?> GetStatusAsync(string sessionId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(respond());
        }
    }

    // Without a registered token the runner returns before sending, so a "sends nothing" assertion
    // would hold even if the tick wrongly ran as steady state. Every silence test needs one.
    private static async Task SeedTokenAsync(string dbName, TimeProvider clock)
    {
        await using var db = NewInMemoryContext(dbName);
        db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBootstrappedAsync(string dbName, TimeProvider clock, string? sessionHash = null)
    {
        // The AccountSyncState row is what flips the first-sync check (R25); the single Debit row
        // is only there so the account looks lived-in — its own payee/content is irrelevant.
        await using var db = NewInMemoryContext(dbName);
        db.AccountSyncStates.Add(new AccountSyncState
        {
            UserId = SeedData.UserId,
            AccountKey = AccountKey,
            FirstSyncCompletedAt = clock.GetUtcNow().AddDays(-30),
            LastSessionIdHash = sessionHash,
        });
        db.Debits.Add(new Debit
        {
            UserId = SeedData.UserId,
            Id = "seed",
            PayeeId = "name:SEED",
            AmountEUR = 1m,
            Timestamp = clock.GetUtcNow().AddDays(-30),
            HasTime = false,
            PaymentType = "Card payment",
            AccountKey = AccountKey,
            FirstSeenAt = clock.GetUtcNow().AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    // --- No active consent ---------------------------------------------------------------------

    [Fact]
    public async Task With_no_active_consent_the_tick_is_a_no_op()
    {
        var dbName = Guid.NewGuid().ToString();
        var fetcher = new FakeDebitsFetcher(Mapped());
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var consent = NewConsentStore(TimeProvider.System, connected: false);

        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, TimeProvider.System);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, fetcher.CallCount);
        Assert.Empty(sender.Sent);
    }

    // --- Bootstrap tick --------------------------------------------------------------------------

    [Fact]
    public async Task A_bootstrap_tick_inserts_debits_and_sends_nothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var mapped = Mapped(Booked("2026-09-20", "NEW SHOP"), Booked("2026-09-21", "NEW SHOP"));
        var fetcher = new FakeDebitsFetcher(mapped);
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var consent = NewConsentStore(clock);

        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(2, await verify.Debits.CountAsync(d => d.UserId == SeedData.UserId));
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    [Fact]
    public async Task A_bootstrap_tick_sets_payee_FirstSeenAt_to_the_earliest_debit_in_the_fetch()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var mapped = Mapped(Booked("2026-06-01", "NEW SHOP"), Booked("2026-09-21", "NEW SHOP"));
        var fetcher = new FakeDebitsFetcher(mapped);
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var consent = NewConsentStore(clock);

        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);
        await runner.RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.UserId == SeedData.UserId);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), payee.FirstSeenAt);
    }

    [Fact]
    public async Task An_existing_payee_row_from_step_4s_synthetic_now_gets_overwritten_with_the_real_earlier_date()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

        // Simulates POST /rules having created the payee first, with a synthetic "now" FirstSeenAt
        // (NOTIFICATION-TRACER-BULLET.md step 4's "as built" note).
        await using (var db = NewInMemoryContext(dbName))
        {
            db.Payees.Add(new Payee
            {
                UserId = SeedData.UserId, Id = "name:PRE EXISTING", Name = "Pre Existing", Initials = "PE",
                FirstSeenAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        var mapped = Mapped(Booked("2026-01-10", "PRE EXISTING"));
        var fetcher = new FakeDebitsFetcher(mapped);
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var consent = NewConsentStore(clock);

        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);
        await runner.RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "name:PRE EXISTING");
        Assert.Equal(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero), payee.FirstSeenAt);
    }

    // --- First sync (R25): the marker row, not row existence, is the mode signal ------------------

    [Fact]
    public async Task A_first_sync_inserts_every_debit_sends_nothing_and_writes_the_marker()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        await SeedTokenAsync(dbName, clock);
        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-20", "SHOP A"), Booked("2026-09-21", "SHOP B")));
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));

        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), fetcher, sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(2, await verify.Debits.CountAsync());
        var state = await verify.AccountSyncStates.SingleAsync();
        Assert.Equal(AccountKey, state.AccountKey);
        Assert.Equal(clock.GetUtcNow(), state.FirstSyncCompletedAt);
    }

    [Fact]
    public async Task An_empty_account_still_completes_its_first_sync_so_the_next_charge_notifies()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);
        var factory = new InMemoryDbContextFactory(dbName);

        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), new FakeSender(_ => SendResult.Accepted("x")), clock)
            .RunOnceAsync(CancellationToken.None);

        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Empty(await verify.Debits.ToListAsync());
            Assert.Single(await verify.AccountSyncStates.ToListAsync());
        }

        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "FIRST REAL CHARGE"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
    }

    /// <summary>
    /// The gap this marker closes: debits left behind by a first sync that never finished, with no
    /// marker. The old "any Debits rows?" check read that as steady state and pushed the rest of
    /// the history.
    /// </summary>
    [Fact]
    public async Task Leftover_debits_from_an_interrupted_first_sync_are_completed_silently()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var mapped = Mapped(Booked("2026-09-20", "OLD SHOP", entryReference: "REF-1"), Booked("2026-09-21", "OLD SHOP", entryReference: "REF-2"));

        await using (var db = NewInMemoryContext(dbName))
        {
            db.Debits.Add(new Debit
            {
                UserId = SeedData.UserId,
                Id = mapped.Payload.Debits[0].Id,
                PayeeId = "name:OLD SHOP",
                AmountEUR = 10m,
                Timestamp = clock.GetUtcNow().AddDays(-2),
                HasTime = false,
                PaymentType = "Card payment",
                AccountKey = AccountKey,
                FirstSeenAt = clock.GetUtcNow().AddDays(-2),
            });
            await db.SaveChangesAsync();
        }

        await SeedTokenAsync(dbName, clock);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(2, await verify.Debits.CountAsync());
        Assert.True(await verify.Debits.AnyAsync(d => d.Id == mapped.Payload.Debits[1].Id));
        Assert.Single(await verify.AccountSyncStates.ToListAsync());
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    private sealed class ThrowingDebitsFetcher : IDebitsFetcher
    {
        public Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct) =>
            throw new HttpRequestException("bank unreachable");
    }

    [Fact]
    public async Task A_fetch_failure_writes_nothing_and_the_next_tick_is_still_a_first_sync()
    {
        // Proves only the fetch-fails-before-any-write path; the atomicity of the first sync's single
        // SaveChangesAsync is not exercised here (no clean save-failure seam over the in-memory provider).
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);
        var factory = new InMemoryDbContextFactory(dbName);
        await SeedTokenAsync(dbName, clock);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewRunner(factory, consent, new ThrowingDebitsFetcher(), sender, clock).RunOnceAsync(CancellationToken.None));

        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Empty(await verify.Debits.ToListAsync());
            Assert.Empty(await verify.AccountSyncStates.ToListAsync());
        }

        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped(Booked("2026-09-21", "SHOP A"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify2 = NewInMemoryContext(dbName);
        Assert.Single(await verify2.Debits.ToListAsync());
        Assert.Single(await verify2.AccountSyncStates.ToListAsync());
    }

    [Fact]
    public async Task A_second_account_of_the_same_user_gets_its_own_first_sync_while_the_first_stays_steady()
    {
        const string secondKey = "DE99888877776666555544";
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var factory = new InMemoryDbContextFactory(dbName);
        await SeedBootstrappedAsync(dbName, clock);
        await SeedTokenAsync(dbName, clock);

        var secondAccount = new ConnectedAccount("uid-2", secondKey, "Second Account", "EUR", null);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        await NewRunner(
                factory,
                NewConsentStore(clock, account: secondAccount),
                new FakeDebitsFetcher(MappedFor(secondKey, Booked("2026-09-21", "OTHER BANK SHOP"))),
                sender,
                clock)
            .RunOnceAsync(CancellationToken.None);

        // The second account ran silently as a first sync (no push) although the user already has a
        // completed one for the first account.
        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        var keys = await verify.AccountSyncStates.Select(s => s.AccountKey).ToListAsync();
        Assert.Equal(2, keys.Count);
        Assert.Contains(AccountKey, keys);
        Assert.Contains(secondKey, keys);
        Assert.Equal(1, await verify.Debits.CountAsync(d => d.AccountKey == secondKey));
    }

    // --- Steady state ----------------------------------------------------------------------------

    [Fact]
    public async Task A_new_debit_for_a_payee_with_no_rule_sends_one_push_and_logs_it()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP")));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        Assert.Equal("NEW SHOP · 10,00 €", sender.Sent[0].Title);

        await using var verify = NewInMemoryContext(dbName);
        var log = await verify.NotificationLog.SingleAsync(n => n.UserId == SeedData.UserId);
        Assert.Equal("New payee — you haven't seen this one before.", log.Reason);
    }

    [Fact]
    public async Task A_good_under_limit_debit_sends_nothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.Payees.Add(new Payee { UserId = SeedData.UserId, Id = "name:GOOD SHOP", Name = "Good Shop", Initials = "GS", FirstSeenAt = clock.GetUtcNow() });
            db.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = "name:GOOD SHOP", Classification = Classification.Good, AmountEUR = 50m, UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "GOOD SHOP", amount: "10.00")));
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);

        await using var verify = NewInMemoryContext(dbName);
        // Still recorded as seen — a rule change later must not retroactively evaluate it again.
        Assert.True(await verify.Debits.AnyAsync(d => d.PayeeId == "name:GOOD SHOP"));
    }

    [Fact]
    public async Task A_good_over_limit_debit_sends_the_over_limit_reason()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.Payees.Add(new Payee { UserId = SeedData.UserId, Id = "name:LIMIT SHOP", Name = "Limit Shop", Initials = "LS", FirstSeenAt = clock.GetUtcNow() });
            db.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = "name:LIMIT SHOP", Classification = Classification.Good, AmountEUR = 30m, UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "LIMIT SHOP", amount: "49.00")));
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        Assert.Contains("Over your limit of 30,00 €.", sender.Sent[0].Body);
    }

    [Fact]
    public async Task A_debit_marked_bad_sends_the_marked_bad_reason_regardless_of_a_leftover_limit()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.Payees.Add(new Payee { UserId = SeedData.UserId, Id = "name:BAD SHOP", Name = "Bad Shop", Initials = "BS", FirstSeenAt = clock.GetUtcNow() });
            // A leftover limit from when the payee was good (R8a) must not be consulted — the A1 bug.
            db.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = "name:BAD SHOP", Classification = Classification.Bad, AmountEUR = 1000m, UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "BAD SHOP", amount: "1.00")));
        var sender = new FakeSender(_ => SendResult.Accepted("x"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        Assert.Equal("You marked this payee as bad.", sender.Sent[0].Body);
    }

    [Fact]
    public async Task An_already_seen_debit_is_never_re_evaluated()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        var mapped = Mapped(Booked("2026-09-22", "REPEAT SHOP", entryReference: "REF-1"));

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.Debits.Add(new Debit
            {
                UserId = SeedData.UserId,
                Id = mapped.Payload.Debits[0].Id,
                PayeeId = "name:REPEAT SHOP",
                AmountEUR = 10m,
                Timestamp = clock.GetUtcNow(),
                HasTime = false,
                PaymentType = "Card payment",
                AccountKey = AccountKey,
                FirstSeenAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(mapped);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(1, await verify.Debits.CountAsync(d => d.PayeeId == "name:REPEAT SHOP"));
    }

    [Fact]
    public async Task A_debit_already_logged_is_not_sent_again_even_if_somehow_reprocessed()
    {
        // R11's belt-and-braces guard: a NotificationLog row for a debit id must stop a send even
        // when the debit row itself is (hypothetically) revisited — checked independently of the
        // "already in Debits" short-circuit the previous test covers.
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        var mapped = Mapped(Booked("2026-09-22", "ALREADY LOGGED"));
        var debitId = mapped.Payload.Debits[0].Id;

        await using (var db = NewInMemoryContext(dbName))
        {
            db.Payees.Add(new Payee { UserId = SeedData.UserId, Id = "name:ALREADY LOGGED", Name = "Already Logged", Initials = "AL", FirstSeenAt = clock.GetUtcNow() });
            db.NotificationLog.Add(new NotificationLogEntry
            {
                Id = Guid.NewGuid(), UserId = SeedData.UserId, DebitId = debitId, SentAt = clock.GetUtcNow(), Reason = "New payee — you haven't seen this one before.",
            });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(mapped);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    /// <summary>
    /// The exact scenario the second 2026-09-22 review found and this fix closes: a cancellation
    /// (the tick's own timeout, or a host shutdown) striking right as/after a send reaches FCM must
    /// not leave the debit half-committed — either the debit and its outcome are both saved, or
    /// neither is, so the next tick sees it as still-new rather than silently skipping it forever.
    /// </summary>
    [Fact]
    public async Task A_cancellation_right_after_a_send_leaves_nothing_committed_and_is_retried_next_tick()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "CUTOFF SHOP")));
        using var cts = new CancellationTokenSource();
        var sendCount = 0;

        // Simulates FirebaseNotificationSender's own documented behaviour: a caller cancellation
        // (the tick's deadline, or shutdown) propagates out of SendAsync as OperationCanceledException,
        // whether or not FCM itself actually accepted the message first.
        var sender = new FakeSender(_ =>
        {
            sendCount++;
            if (sendCount == 1)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return SendResult.Accepted("msg-1");
        });
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunOnceAsync(cts.Token));

        Assert.Equal(1, sendCount);
        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.False(await verify.Debits.AnyAsync(d => d.PayeeId == "name:CUTOFF SHOP"));
            Assert.Empty(await verify.NotificationLog.ToListAsync());
        }

        // A later, uncancelled tick (IngestionWorker's next timer fire) sees the same debit as
        // still-new — because nothing committed for it — and retries the whole thing, this time to
        // completion.
        var retryRunner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);
        await retryRunner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sendCount); // the cancelled attempt, plus this successful retry
        await using var verify2 = NewInMemoryContext(dbName);
        Assert.True(await verify2.Debits.AnyAsync(d => d.PayeeId == "name:CUTOFF SHOP"));
        Assert.Equal(1, await verify2.NotificationLog.CountAsync(n => n.UserId == SeedData.UserId));
    }

    // --- Reconnect detection: the session fingerprint ---------------------------------------------

    [Fact]
    public async Task A_first_sync_stores_the_session_hash_not_the_session_id()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedTokenAsync(dbName, clock);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));

        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-21", "SHOP A"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var stored = (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash;
        Assert.NotNull(stored);
        Assert.Equal(64, stored.Length);
        Assert.Matches("^[0-9a-f]{64}$", stored);
        Assert.NotEqual("session-1", stored);
        Assert.Equal(SessionFingerprint.Of("session-1"), stored);
    }

    [Fact]
    public async Task A_row_with_a_null_hash_adopts_the_session_silently_and_still_pushes_a_new_bad_debit()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock); // null hash: stored before the column existed
        await SeedTokenAsync(dbName, clock);

        var mapped = Mapped(
            Booked("2026-09-20", "OLD SHOP", entryReference: "REF-OLD"),
            Booked("2026-09-22", "NEW SHOP", entryReference: "REF-NEW"));
        await using (var db = NewInMemoryContext(dbName))
        {
            // The already-stored history: must not push again just because the hash is being adopted.
            db.Debits.Add(new Debit
            {
                UserId = SeedData.UserId, Id = mapped.Payload.Debits[0].Id, PayeeId = "name:OLD SHOP", AmountEUR = 10m,
                Timestamp = clock.GetUtcNow().AddDays(-2), HasTime = false, PaymentType = "Card payment",
                AccountKey = AccountKey, FirstSeenAt = clock.GetUtcNow().AddDays(-2),
            });
            await db.SaveChangesAsync();
        }

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(sender.Sent);
        Assert.StartsWith("NEW SHOP", push.Title);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(SessionFingerprint.Of("session-1"), (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
    }

    [Fact]
    public async Task The_same_session_leaves_the_hash_unchanged()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var hash = SessionFingerprint.Of("session-1");
        await SeedBootstrappedAsync(dbName, clock, hash);
        await SeedTokenAsync(dbName, clock);

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(hash, (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
    }

    private static async Task SeedRuleAsync(string dbName, TimeProvider clock, string payeeName, Classification classification, decimal? limit = null)
    {
        await using var db = NewInMemoryContext(dbName);
        var payeeId = $"name:{payeeName}";
        db.Payees.Add(new Payee { UserId = SeedData.UserId, Id = payeeId, Name = payeeName, Initials = "XX", FirstSeenAt = clock.GetUtcNow() });
        db.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = payeeId, Classification = classification, AmountEUR = limit, UpdatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private static async Task SeedReconnectAsync(string dbName, TimeProvider clock)
    {
        await SeedBootstrappedAsync(dbName, clock, SessionFingerprint.Of("old-session"));
        await SeedTokenAsync(dbName, clock);
    }

    private static async Task AssertCommittedAsync(string dbName, int newDebits)
    {
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(SessionFingerprint.Of("session-1"), (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
        Assert.Equal(1 + newDebits, await verify.Debits.CountAsync()); // the seeded one plus the new ones
        Assert.Empty(await verify.NotificationLog.ToListAsync()); // the summary belongs to no single debit
    }

    // --- Reconnect summary (R20/R20a) ---------------------------------------------------------------

    [Fact]
    public async Task A_reconnect_with_bad_debits_sends_one_summary_and_commits_everything()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);
        await SeedRuleAsync(dbName, clock, "BAD SHOP", Classification.Bad);
        await SeedRuleAsync(dbName, clock, "GOOD SHOP", Classification.Good, limit: 50m);

        var mapped = Mapped(
            Booked("2026-09-20", "NO RULE SHOP", entryReference: "R1"),
            Booked("2026-09-21", "BAD SHOP", entryReference: "R2"),
            Booked("2026-09-22", "GOOD SHOP", entryReference: "R3"));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(sender.Sent);
        Assert.Equal("3 new debits while you were disconnected", push.Title);
        Assert.Equal("2 of them are bad. Tap to review them.", push.Body);
        await AssertCommittedAsync(dbName, newDebits: 3);
    }

    [Fact]
    public async Task A_reconnect_with_only_good_debits_sends_nothing_but_commits()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);
        await SeedRuleAsync(dbName, clock, "GOOD SHOP", Classification.Good, limit: 50m);

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "GOOD SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await AssertCommittedAsync(dbName, newDebits: 1);
    }

    [Fact]
    public async Task A_reconnect_with_exactly_one_bad_debit_still_uses_the_summary_wording()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(sender.Sent);
        Assert.Equal("1 new debits while you were disconnected", push.Title);
        Assert.Equal("1 of them are bad. Tap to review them.", push.Body);
        await AssertCommittedAsync(dbName, newDebits: 1);
    }

    [Fact]
    public async Task A_reconnect_with_no_new_debits_sends_nothing_and_updates_the_hash()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);

        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await AssertCommittedAsync(dbName, newDebits: 0);
    }

    [Fact]
    public async Task A_reconnect_tick_aborted_after_the_send_commits_nothing_and_the_next_tick_sends_the_same_summary()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var oldHash = SessionFingerprint.Of("old-session");
        await SeedReconnectAsync(dbName, clock);

        var mapped = Mapped(
            Booked("2026-09-21", "SHOP A", entryReference: "R1"),
            Booked("2026-09-22", "SHOP B", entryReference: "R2"));
        var fetcher = new FakeDebitsFetcher(mapped);
        var consent = NewConsentStore(clock);
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeSender(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, cancelling, clock).RunOnceAsync(cts.Token));

        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Equal(oldHash, (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
            Assert.Equal(1, await verify.Debits.CountAsync()); // only the seeded one: no new debit was committed
        }

        var retry = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, retry, clock).RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(retry.Sent);
        Assert.Equal("2 new debits while you were disconnected", push.Title);
        Assert.Equal("2 of them are bad. Tap to review them.", push.Body);
        await AssertCommittedAsync(dbName, newDebits: 2);
    }

    /// <summary>The same fetch with its first debit repeated (same id), as a misbehaving bank page could deliver.</summary>
    private static MappedDebits WithFirstDebitDuplicated(MappedDebits m) =>
        m with { Payload = m.Payload with { Debits = [.. m.Payload.Debits, m.Payload.Debits[0]] } };

    [Fact]
    public async Task A_reconnect_with_a_duplicate_id_in_one_fetch_counts_it_once_and_still_completes()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);

        var mapped = WithFirstDebitDuplicated(Mapped(
            Booked("2026-09-21", "SHOP A", entryReference: "R1"),
            Booked("2026-09-22", "SHOP B", entryReference: "R2")));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(sender.Sent);
        Assert.Equal("2 new debits while you were disconnected", push.Title);
        Assert.Equal("2 of them are bad. Tap to review them.", push.Body);
        await AssertCommittedAsync(dbName, newDebits: 2);
    }

    [Fact]
    public async Task A_first_sync_with_a_duplicate_id_in_one_fetch_stores_it_once_and_writes_the_marker()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedTokenAsync(dbName, clock);

        var mapped = WithFirstDebitDuplicated(Mapped(Booked("2026-09-21", "SHOP A", entryReference: "R1")));
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        Assert.Single(await verify.AccountSyncStates.ToListAsync());
        Assert.Equal(1, await verify.Debits.CountAsync());
    }

    [Fact]
    public async Task A_reconnect_debit_over_a_good_payees_limit_counts_as_bad_and_uses_the_summary_wording()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);
        await SeedRuleAsync(dbName, clock, "LIMIT SHOP", Classification.Good, limit: 30m);

        var mapped = Mapped(
            Booked("2026-09-21", "LIMIT SHOP", amount: "45.00", entryReference: "R1"),
            Booked("2026-09-22", "LIMIT SHOP", amount: "10.00", entryReference: "R2"));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(mapped), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(sender.Sent);
        Assert.Equal("2 new debits while you were disconnected", push.Title);
        Assert.Equal("1 of them are bad. Tap to review them.", push.Body);
        await AssertCommittedAsync(dbName, newDebits: 2);
    }

    [Theory]
    [InlineData(SendOutcome.Rejected)]
    [InlineData(SendOutcome.Transient)]
    public async Task A_failed_summary_send_still_commits_debits_and_hash(SendOutcome outcome)
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);

        var sender = new FakeSender(_ => SendResult.Failed(outcome, "boom"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        await AssertCommittedAsync(dbName, newDebits: 1);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(1, await verify.DeviceTokens.CountAsync()); // never pruned
    }

    [Fact]
    public async Task A_reconnect_summary_prunes_only_the_dead_token_in_the_same_save()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, SessionFingerprint.Of("old-session"));
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "dead-token", UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "live-token", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var sender = new FakeSender(token => token == "dead-token"
            ? SendResult.Failed(SendOutcome.TokenNoLongerValid, "gone")
            : SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        await AssertCommittedAsync(dbName, newDebits: 1);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal("live-token", (await verify.DeviceTokens.SingleAsync()).Token);
    }

    [Fact]
    public async Task A_reconnect_with_bad_debits_and_no_device_token_still_commits()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, SessionFingerprint.Of("old-session")); // no token on purpose

        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await AssertCommittedAsync(dbName, newDebits: 1);
    }

    [Fact]
    public async Task After_the_reconnect_tick_the_same_session_is_steady_and_a_new_bad_debit_pushes_individually()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedReconnectAsync(dbName, clock);
        var consent = NewConsentStore(clock);

        var first = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), consent, new FakeDebitsFetcher(Mapped(Booked("2026-09-21", "SHOP A", entryReference: "R1"))), first, clock)
            .RunOnceAsync(CancellationToken.None);
        Assert.Single(first.Sent);

        var second = new FakeSender(_ => SendResult.Accepted("msg-2"));
        var mapped = Mapped(
            Booked("2026-09-21", "SHOP A", entryReference: "R1"),
            Booked("2026-09-23", "SHOP B", entryReference: "R2"));
        await NewRunner(new InMemoryDbContextFactory(dbName), consent, new FakeDebitsFetcher(mapped), second, clock)
            .RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(second.Sent);
        Assert.StartsWith("SHOP B", push.Title);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(1, await verify.NotificationLog.CountAsync());
    }

    [Fact]
    public async Task A_different_session_and_an_aborted_tick_keep_the_old_hash_so_the_next_tick_detects_it_again()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var oldHash = SessionFingerprint.Of("old-session");
        await SeedBootstrappedAsync(dbName, clock, oldHash);
        await SeedTokenAsync(dbName, clock);

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "CUTOFF SHOP")));
        using var cts = new CancellationTokenSource();
        var sender = new FakeSender(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var consent = NewConsentStore(clock);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock).RunOnceAsync(cts.Token));

        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Equal(oldHash, (await verify.AccountSyncStates.SingleAsync()).LastSessionIdHash);
        }

        // The next tick still sees the reconnect, completes, and only then moves the hash.
        await NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, new FakeSender(_ => SendResult.Accepted("msg-1")), clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify2 = NewInMemoryContext(dbName);
        Assert.Equal(SessionFingerprint.Of("session-1"), (await verify2.AccountSyncStates.SingleAsync()).LastSessionIdHash);
    }

    [Fact]
    public async Task Two_accounts_each_keep_their_own_session_hash()
    {
        const string secondKey = "DE99888877776666555544";
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var factory = new InMemoryDbContextFactory(dbName);
        await SeedTokenAsync(dbName, clock);
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        var secondAccount = new ConnectedAccount("uid-2", secondKey, "Second Account", "EUR", null);

        await NewRunner(factory, NewConsentStore(clock), new FakeDebitsFetcher(Mapped(Booked("2026-09-21", "SHOP A"))), sender, clock)
            .RunOnceAsync(CancellationToken.None);
        await NewRunner(
                factory,
                NewConsentStore(clock, account: secondAccount, sessionId: "session-2"),
                new FakeDebitsFetcher(MappedFor(secondKey, Booked("2026-09-21", "SHOP B"))),
                sender,
                clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var hashes = await verify.AccountSyncStates.ToDictionaryAsync(s => s.AccountKey, s => s.LastSessionIdHash);
        Assert.Equal(SessionFingerprint.Of("session-1"), hashes[AccountKey]);
        Assert.Equal(SessionFingerprint.Of("session-2"), hashes[secondKey]);
    }

    [Fact]
    public void DetermineMode_maps_stored_state_to_a_mode()
    {
        var hash = SessionFingerprint.Of("session-1");
        AccountSyncState Row(string? stored) => new() { UserId = SeedData.UserId, AccountKey = AccountKey, LastSessionIdHash = stored };

        Assert.Equal(IngestionRunner.IngestionMode.FirstSync, IngestionRunner.DetermineMode(null, hash));
        Assert.Equal(IngestionRunner.IngestionMode.Steady, IngestionRunner.DetermineMode(Row(null), hash));
        Assert.Equal(IngestionRunner.IngestionMode.Steady, IngestionRunner.DetermineMode(Row(hash), hash));
        Assert.Equal(IngestionRunner.IngestionMode.Reconnect, IngestionRunner.DetermineMode(Row(SessionFingerprint.Of("other")), hash));
    }

    // --- Multi-token outcomes ----------------------------------------------------------------------

    [Fact]
    public async Task TokenNoLongerValid_on_one_of_two_tokens_prunes_only_that_token_while_the_other_still_logs_Sent()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "dead-token", UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "live-token", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW PAYEE")));
        var sender = new FakeSender(token => token == "dead-token"
            ? SendResult.Failed(SendOutcome.TokenNoLongerValid, "Unregistered")
            : SendResult.Accepted("msg-1"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);

        await using var verify = NewInMemoryContext(dbName);
        Assert.False(await verify.DeviceTokens.AnyAsync(t => t.Token == "dead-token"));
        Assert.True(await verify.DeviceTokens.AnyAsync(t => t.Token == "live-token"));
        Assert.Equal(1, await verify.NotificationLog.CountAsync(n => n.UserId == SeedData.UserId));
    }

    [Fact]
    public async Task Rejected_and_Transient_write_no_log_row_and_never_prune()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-1", UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "tok-2", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "REJECTED PAYEE")));
        var sender = new FakeSender(token => token == "tok-1"
            ? SendResult.Failed(SendOutcome.Rejected, "InvalidArgument")
            : SendResult.Failed(SendOutcome.Transient, "Unavailable"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        Assert.Empty(await verify.NotificationLog.ToListAsync());
        Assert.True(await verify.DeviceTokens.AnyAsync(t => t.Token == "tok-1"));
        Assert.True(await verify.DeviceTokens.AnyAsync(t => t.Token == "tok-2"));
    }

    [Fact]
    public async Task With_no_device_token_registered_a_bad_debit_is_still_recorded_but_nothing_is_sent()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await SeedBootstrappedAsync(dbName, clock);

        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NO TOKEN YET")));
        var sender = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        var runner = NewRunner(new InMemoryDbContextFactory(dbName), consent, fetcher, sender, clock);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.True(await verify.Debits.AnyAsync(d => d.PayeeId == "name:NO TOKEN YET"));
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    // --- Dead consent (R19): the time signal ---------------------------------------------------------

    private static readonly string Session1Hash = SessionFingerprint.Of("session-1");

    /// <summary>A store whose session-1 consent died at <paramref name="clock"/>'s "now" (>= counts as expired).</summary>
    private static ConsentStore NewExpiredConsentStore(TimeProvider clock, string sessionId = "session-1") =>
        NewConsentStore(clock, sessionId: sessionId, expiresAt: clock.GetUtcNow());

    private static async Task<string?> ExpiryGuardAsync(string dbName)
    {
        await using var verify = NewInMemoryContext(dbName);
        return (await verify.AccountSyncStates.SingleAsync()).ExpiryNotifiedSessionHash;
    }

    private static void AssertExpiryPush(PushMessage push)
    {
        Assert.Equal("Bank connection expired", push.Title);
        Assert.Equal("Reconnect to keep getting alerts.", push.Body);
    }

    [Fact]
    public async Task An_expired_consent_pushes_once_without_fetching_and_sets_the_guard()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);
        var fetcher = new FakeDebitsFetcher(Mapped(Booked("2026-09-22", "NEW SHOP")));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        await NewRunner(new InMemoryDbContextFactory(dbName), NewExpiredConsentStore(clock), fetcher, sender, clock)
            .RunOnceAsync(CancellationToken.None);

        AssertExpiryPush(Assert.Single(sender.Sent));
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(Session1Hash, await ExpiryGuardAsync(dbName));
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(1, await verify.Debits.CountAsync()); // only the seeded one: nothing was ingested
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }

    [Fact]
    public async Task A_second_tick_on_the_same_dead_session_does_not_push_again()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);
        var consent = NewExpiredConsentStore(clock);
        var factory = new InMemoryDbContextFactory(dbName);
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), sender, clock).RunOnceAsync(CancellationToken.None);
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), sender, clock).RunOnceAsync(CancellationToken.None);

        AssertExpiryPush(Assert.Single(sender.Sent));
    }

    [Theory]
    [InlineData(SendOutcome.Rejected)]
    [InlineData(SendOutcome.Transient)]
    public async Task A_failed_expiry_push_leaves_the_guard_empty_and_the_next_tick_sends_again(SendOutcome outcome)
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);
        var consent = NewExpiredConsentStore(clock);
        var factory = new InMemoryDbContextFactory(dbName);

        var failing = new FakeSender(_ => SendResult.Failed(outcome, "boom"));
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), failing, clock).RunOnceAsync(CancellationToken.None);

        Assert.Single(failing.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Equal(1, await verify.DeviceTokens.CountAsync()); // never pruned
        }

        var working = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), working, clock).RunOnceAsync(CancellationToken.None);

        AssertExpiryPush(Assert.Single(working.Sent));
        Assert.Equal(Session1Hash, await ExpiryGuardAsync(dbName));
    }

    [Fact]
    public async Task A_dead_token_is_pruned_while_the_live_one_sends_and_sets_the_guard()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await using (var db = NewInMemoryContext(dbName))
        {
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "dead-token", UpdatedAt = clock.GetUtcNow() });
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = "live-token", UpdatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var sender = new FakeSender(token => token == "dead-token"
            ? SendResult.Failed(SendOutcome.TokenNoLongerValid, "gone")
            : SendResult.Accepted("msg-1"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewExpiredConsentStore(clock), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(Session1Hash, await ExpiryGuardAsync(dbName));
        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal("live-token", (await verify.DeviceTokens.SingleAsync()).Token);
    }

    [Fact]
    public async Task Only_a_dead_token_is_pruned_and_the_guard_stays_empty()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);

        var sender = new FakeSender(_ => SendResult.Failed(SendOutcome.TokenNoLongerValid, "gone"));
        await NewRunner(new InMemoryDbContextFactory(dbName), NewExpiredConsentStore(clock), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
        await using var verify = NewInMemoryContext(dbName);
        Assert.Empty(await verify.DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task With_no_device_token_the_guard_stays_empty_and_a_later_registration_gets_the_push()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash); // no token on purpose
        var consent = NewExpiredConsentStore(clock);
        var factory = new InMemoryDbContextFactory(dbName);

        var silent = new FakeSender(_ => throw new InvalidOperationException("must not be called"));
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), silent, clock).RunOnceAsync(CancellationToken.None);

        Assert.Empty(silent.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));

        await SeedTokenAsync(dbName, clock);
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));
        await NewRunner(factory, consent, new FakeDebitsFetcher(Mapped()), sender, clock).RunOnceAsync(CancellationToken.None);

        AssertExpiryPush(Assert.Single(sender.Sent));
        Assert.Equal(Session1Hash, await ExpiryGuardAsync(dbName));
    }

    [Fact]
    public async Task An_expired_consent_with_no_sync_state_row_sends_nothing_and_does_not_crash()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedTokenAsync(dbName, clock); // a token, so a wrongly sent push would show
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        await NewRunner(new InMemoryDbContextFactory(dbName), NewExpiredConsentStore(clock), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        await using var verify = NewInMemoryContext(dbName);
        Assert.Empty(await verify.AccountSyncStates.ToListAsync());
    }

    [Fact]
    public async Task A_new_session_after_the_death_reconnects_and_its_own_later_death_pushes_again()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);
        var factory = new InMemoryDbContextFactory(dbName);
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        await NewRunner(factory, NewExpiredConsentStore(clock), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);
        Assert.Single(sender.Sent);

        // The user logs in again: a new session, live. The tick takes the reconnect path (hash moves).
        var session2Hash = SessionFingerprint.Of("session-2");
        var fetcher = new FakeDebitsFetcher(Mapped());
        await NewRunner(factory, NewConsentStore(clock, sessionId: "session-2"), fetcher, sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, fetcher.CallCount);
        Assert.Single(sender.Sent); // an empty reconnect sends no summary (R20a)
        await using (var verify = NewInMemoryContext(dbName))
        {
            var row = await verify.AccountSyncStates.SingleAsync();
            Assert.Equal(session2Hash, row.LastSessionIdHash);
            Assert.Equal(Session1Hash, row.ExpiryNotifiedSessionHash);
        }

        // The new session dies in its turn: its hash differs from the guard, so it announces again.
        await NewRunner(factory, NewExpiredConsentStore(clock, sessionId: "session-2"), new FakeDebitsFetcher(Mapped()), sender, clock)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        AssertExpiryPush(sender.Sent[1]);
        Assert.Equal(session2Hash, await ExpiryGuardAsync(dbName));
    }

    // --- Dead consent (R19): the bank signal ---------------------------------------------------------

    private sealed class FailingDebitsFetcher(Exception exception) : IDebitsFetcher
    {
        public Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct) => throw exception;
    }

    private static async Task<(string DbName, FixedTimeProvider Clock, InMemoryDbContextFactory Factory)> SeedLiveSessionAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await SeedBootstrappedAsync(dbName, clock, Session1Hash);
        await SeedTokenAsync(dbName, clock);
        return (dbName, clock, new InMemoryDbContextFactory(dbName));
    }

    [Fact]
    public async Task A_401_with_an_error_code_and_a_CLOSED_session_pushes_once_and_sets_the_guard()
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var fetcher = new FailingDebitsFetcher(new EnableBankingRequestException(401, "CLOSED_SESSION"));
        var checker = new FakeSessionStatusChecker(() => "CLOSED");
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        await NewRunner(factory, NewConsentStore(clock), fetcher, sender, clock, checker).RunOnceAsync(CancellationToken.None);

        AssertExpiryPush(Assert.Single(sender.Sent));
        Assert.Equal(1, checker.CallCount);
        Assert.Equal(Session1Hash, await ExpiryGuardAsync(dbName));

        // The next tick fails the same way and must stay quiet.
        await NewRunner(factory, NewConsentStore(clock), fetcher, sender, clock, checker).RunOnceAsync(CancellationToken.None);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_401_with_an_error_code_but_an_AUTHORIZED_session_pushes_nothing_and_rethrows_the_original()
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var original = new EnableBankingRequestException(401, "CLOSED_SESSION");
        var checker = new FakeSessionStatusChecker(() => "AUTHORIZED");
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        var thrown = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewRunner(factory, NewConsentStore(clock), new FailingDebitsFetcher(original), sender, clock, checker).RunOnceAsync(CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Equal(1, checker.CallCount);
        Assert.Empty(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
    }

    [Fact]
    public async Task A_401_without_an_error_code_our_own_key_never_asks_the_checker_and_never_pushes()
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var original = new EnableBankingRequestException(401, null);
        var checker = new FakeSessionStatusChecker(() => "CLOSED"); // would say dead, if it were asked
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        var thrown = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewRunner(factory, NewConsentStore(clock), new FailingDebitsFetcher(original), sender, clock, checker).RunOnceAsync(CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Equal(0, checker.CallCount);
        Assert.Empty(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
    }

    [Theory]
    [InlineData(429, "ASPSP_RATE_LIMIT_EXCEEDED")]
    [InlineData(429, null)]
    [InlineData(500, null)]
    [InlineData(500, "SOME_ERROR")]
    public async Task Other_statuses_never_count_as_dead_even_with_an_error_code(int status, string? errorCode)
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var original = new EnableBankingRequestException(status, errorCode);
        var checker = new FakeSessionStatusChecker(() => "CLOSED");
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        var thrown = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewRunner(factory, NewConsentStore(clock), new FailingDebitsFetcher(original), sender, clock, checker).RunOnceAsync(CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Equal(0, checker.CallCount);
        Assert.Empty(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
    }

    [Fact]
    public async Task An_unknown_status_from_the_checker_pushes_nothing_and_rethrows_the_original()
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var original = new EnableBankingRequestException(401, "CLOSED_SESSION");
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        var thrown = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewRunner(factory, NewConsentStore(clock), new FailingDebitsFetcher(original), sender, clock, new FakeSessionStatusChecker(() => null))
                .RunOnceAsync(CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Empty(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
    }

    [Fact]
    public async Task A_checker_that_throws_pushes_nothing_and_the_original_exception_still_surfaces()
    {
        var (dbName, clock, factory) = await SeedLiveSessionAsync();
        var original = new EnableBankingRequestException(401, "CLOSED_SESSION");
        var checker = new FakeSessionStatusChecker(() => throw new HttpRequestException("unreachable"));
        var sender = new FakeSender(_ => SendResult.Accepted("msg-1"));

        var thrown = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewRunner(factory, NewConsentStore(clock), new FailingDebitsFetcher(original), sender, clock, checker).RunOnceAsync(CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Equal(1, checker.CallCount);
        Assert.Empty(sender.Sent);
        Assert.Null(await ExpiryGuardAsync(dbName));
    }
}
