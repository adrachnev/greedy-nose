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

    private static ConsentStore NewConsentStore(TimeProvider clock, bool connected = true)
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
            store.Complete("session-1", [Account], clock.GetUtcNow(), null);
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

    private static MappedDebits Mapped(params EbTransaction[] transactions) =>
        TransactionMapper.Map(new EbTransactionsResponse(transactions, null), AccountKey);

    private static IngestionRunner NewRunner(
        IDbContextFactory<GreedyNoseDbContext> dbFactory, ConsentStore consent, IDebitsFetcher fetcher,
        INotificationSender sender, TimeProvider clock) =>
        new(dbFactory, consent, fetcher, sender, clock, NullLogger<IngestionRunner>.Instance);

    private static async Task SeedBootstrappedAsync(string dbName, TimeProvider clock)
    {
        // A single pre-existing Debit row for this account is enough to flip the bootstrap check —
        // its own payee/content is irrelevant, only its AccountKey matters.
        await using var db = NewInMemoryContext(dbName);
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
}
