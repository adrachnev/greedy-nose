using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using GreedyNose.Api.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// <c>Payee.RecordSeen</c> and the two independent upsert paths that call it —
/// <c>IngestionRunner.UpsertPayeesAsync</c> and <c>RulesEndpoint.HandleAsync</c> — the distinct-set
/// storage ARCHITECTURE.md's "Payee identity" section asks for (raw material to re-tune R3a's
/// matching key later against what banks actually sent, not a design in itself).
///
/// Split in two: <c>RecordSeen</c> itself is pinned directly, no database, mirroring
/// <c>ClassificationTests.cs</c>'s pure-logic style; the cross-path tests below use the same
/// in-memory-provider plumbing <c>RulesTests.cs</c> and <c>IngestionRunnerTests.cs</c> each already
/// define locally, kept duplicated here rather than shared — neither of those two files can prove
/// the cross-path case alone, since each only ever drives its own endpoint.
/// </summary>
public class PayeeIdentitySeenSetsTests
{
    // --- Payee.RecordSeen, no database -------------------------------------------------------------

    private static Payee NewPayee() => new()
    {
        UserId = SeedData.UserId,
        Id = "iban:DE00111122223333",
        Name = "Shop",
        Initials = "S",
    };

    [Fact]
    public void RecordSeen_adds_a_new_value_to_each_set()
    {
        var payee = NewPayee();

        payee.RecordSeen("DE00111122223333", "SHOP GMBH", "COBADEFFXXX");

        Assert.Equal(["DE00111122223333"], payee.IbansSeen);
        Assert.Equal(["SHOP GMBH"], payee.NormalizedNamesSeen);
        Assert.Equal(["COBADEFFXXX"], payee.CreditorAgentsSeen);
    }

    [Fact]
    public void RecordSeen_appends_a_second_distinct_spelling_rather_than_replacing_the_first()
    {
        var payee = NewPayee();

        payee.RecordSeen(iban: null, normalizedName: "SHOP GMBH", creditorAgent: null);
        payee.RecordSeen(iban: null, normalizedName: "SHOP GMBH FILIALE", creditorAgent: null);

        Assert.Equal(["SHOP GMBH", "SHOP GMBH FILIALE"], payee.NormalizedNamesSeen);
    }

    [Fact]
    public void RecordSeen_does_not_duplicate_a_value_already_in_the_set()
    {
        var payee = NewPayee();

        payee.RecordSeen("DE00111122223333", "SHOP GMBH", null);
        payee.RecordSeen("DE00111122223333", "SHOP GMBH", null);

        Assert.Equal(["DE00111122223333"], payee.IbansSeen);
        Assert.Equal(["SHOP GMBH"], payee.NormalizedNamesSeen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordSeen_never_stores_a_blank_value(string? blank)
    {
        var payee = NewPayee();

        payee.RecordSeen(blank, blank, blank);

        Assert.Empty(payee.IbansSeen);
        Assert.Empty(payee.NormalizedNamesSeen);
        Assert.Empty(payee.CreditorAgentsSeen);
    }

    /// <summary>RulesEndpoint's own case: it never has a creditor agent to offer, but must still
    /// merge the other two sets rather than skip the whole call.</summary>
    [Fact]
    public void RecordSeen_with_no_creditor_agent_still_merges_the_other_two_sets()
    {
        var payee = NewPayee();

        payee.RecordSeen("DE00111122223333", "SHOP GMBH", creditorAgent: null);

        Assert.Equal(["DE00111122223333"], payee.IbansSeen);
        Assert.Equal(["SHOP GMBH"], payee.NormalizedNamesSeen);
        Assert.Empty(payee.CreditorAgentsSeen);
    }

    // --- Cross-path: RulesEndpoint and IngestionRunner merge into the same sets, never clobber ----

    private const string AccountKey = "DE11222233334444555566";
    private const string CreditorIban = "DE00111122223333";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GreedyNoseDbContext NewInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

    private sealed class InMemoryDbContextFactory(string databaseName) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => NewInMemoryContext(databaseName);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static ConsentStore NewConsentStore(TimeProvider clock)
    {
        var store = new ConsentStore(
            Path.Combine(Path.GetTempPath(), $"payee-seen-test-consent-{Guid.NewGuid():N}.json"),
            TimeSpan.FromDays(90),
            NullLogger<ConsentStore>.Instance);

        store.Complete("session-1", [new ConnectedAccount("uid-1", AccountKey, "Test Account", "EUR", null)], clock.GetUtcNow(), null);

        return store;
    }

    private sealed class FakeDebitsFetcher(MappedDebits result) : IDebitsFetcher
    {
        public Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeSender : INotificationSender
    {
        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct) => Task.FromResult(SendResult.Accepted("x"));
    }

    private static IngestionRunner NewRunner(string dbName, ConsentStore consent, IDebitsFetcher fetcher, TimeProvider clock) =>
        new(new InMemoryDbContextFactory(dbName), consent, fetcher, new FakeSessionStatusChecker(), new FakeSender(), clock, NullLogger<IngestionRunner>.Instance);

    private sealed class FakeSessionStatusChecker : ISessionStatusChecker
    {
        public Task<string?> GetStatusAsync(string sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A card debit carrying a creditor IBAN, for the cases IngestionRunnerTests' own
    /// <c>Booked</c> helper does not need (it always sets <c>CreditorAccount: null</c>).</summary>
    private static EbTransaction BookedWithIban(string date, string name, string iban) =>
        new(
            EntryReference: null,
            TransactionAmount: new EbAmount("EUR", "10.00"),
            Creditor: new EbParty(name),
            CreditorAccount: new EbAccountId(iban),
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

    private static RulesRequest RuleFor(string name, string initials) =>
        new(PayeeId: "iban:" + CreditorIban, Classification: "good", AmountEUR: null, Name: name, Initials: initials, Iban: CreditorIban);

    /// <summary>A card debit carrying both a creditor IBAN and a creditor agent/BIC — the one field
    /// <see cref="BookedWithIban"/> itself always leaves null, needed for
    /// <c>CreditorAgentsSeen</c> coverage (nothing in the file so far exercises it end to end).</summary>
    private static EbTransaction BookedWithIbanAndAgent(string date, string name, string iban, string agentBic) =>
        BookedWithIban(date, name, iban) with { CreditorAgent = new EbAgent(agentBic, null) };

    /// <summary>A card debit with a real creditor IBAN but no creditor name at all — the edge case
    /// Finding 1 exists for: <see cref="TransactionMapper.BuildPayee"/>'s own display-name fallback
    /// (the raw IBAN, since this key isn't <c>unknown</c>) must not leak into
    /// <c>NormalizedNamesSeen</c> as a mangled digit-stripped fragment. The <c>name</c>
    /// <see cref="BookedWithIban"/> would otherwise set is discarded immediately by the <c>with</c>
    /// override below.</summary>
    private static EbTransaction BookedWithIbanNoName(string date, string iban) =>
        BookedWithIban(date, name: "", iban) with { Creditor = new EbParty(null) };

    /// <summary>
    /// Two separate fetches, same IBAN, two different remittance spellings — exactly what R3a's
    /// design note expects to see accumulate on an IBAN-keyed payee over time (a name-keyed payee's
    /// set is trivially one entry, since the key itself already commits to one normalized name).
    /// </summary>
    [Fact]
    public async Task Two_ingestion_ticks_with_different_spellings_under_the_same_iban_both_land_in_the_set()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-05", "Shop Spelling One", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);
        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-06", "Shop Spelling Two", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        Assert.Equal(["SHOP SPELLING ONE", "SHOP SPELLING TWO"], payee.NormalizedNamesSeen);
        Assert.Equal([CreditorIban], payee.IbansSeen);
    }

    /// <summary>
    /// The scenario the task exists for: a payee <c>POST /rules</c> creates first, then a later
    /// ingestion tick sees the same payee under a different spelling. The raw <c>Name</c> is still
    /// overwritten (unchanged behaviour) but the sets must grow, not reset.
    /// </summary>
    [Fact]
    public async Task Ingestion_after_rules_merges_the_distinct_sets_rather_than_clobbering_them()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await using (var db = NewInMemoryContext(dbName))
        {
            await RulesEndpoint.HandleAsync(RuleFor("Shop Old Name", "SO"), db, clock, NullLoggerFactory.Instance, CancellationToken.None);
        }

        var consent = NewConsentStore(clock);
        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-10", "Shop New Name", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        Assert.Equal("Shop New Name", payee.Name); // still overwritten — unrelated to the sets
        Assert.Equal([CreditorIban], payee.IbansSeen);
        Assert.Equal(["SHOP OLD NAME", "SHOP NEW NAME"], payee.NormalizedNamesSeen);
        Assert.Empty(payee.CreditorAgentsSeen); // BookedWithIban never carries an agent
    }

    /// <summary>The reverse order: ingestion sees the payee first, and a later <c>POST /rules</c>
    /// for the same payee id merges into the sets ingestion already started.</summary>
    [Fact]
    public async Task Rules_after_ingestion_merges_the_distinct_sets_rather_than_clobbering_them()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-05", "Shop From Bank", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);

        await using (var db = NewInMemoryContext(dbName))
        {
            await RulesEndpoint.HandleAsync(RuleFor("Shop From App", "SA"), db, clock, NullLoggerFactory.Instance, CancellationToken.None);
        }

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        Assert.Equal("Shop From App", payee.Name); // POST /rules' own refresh — unrelated to the sets
        Assert.Equal([CreditorIban], payee.IbansSeen);
        Assert.Equal(["SHOP FROM BANK", "SHOP FROM APP"], payee.NormalizedNamesSeen);
        Assert.Empty(payee.CreditorAgentsSeen);
    }

    /// <summary>
    /// Nothing else in this file ever gives a transaction a creditor agent — every other case runs
    /// through <see cref="BookedWithIban"/>, which hardcodes <c>CreditorAgent: null</c>. Proves the
    /// full <c>IngestionRunner.RunOnceAsync</c> path actually lands a real BIC in
    /// <c>CreditorAgentsSeen</c>, not just <c>Payee.RecordSeen</c> in isolation (already pinned
    /// above, no database).
    /// </summary>
    [Fact]
    public async Task Ingestion_tick_with_a_creditor_agent_records_it_in_CreditorAgentsSeen()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await NewRunner(
                dbName, consent,
                new FakeDebitsFetcher(Mapped(BookedWithIbanAndAgent("2026-01-05", "Shop With Agent", CreditorIban, "COBADEFFXXX"))),
                clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        Assert.Equal(["COBADEFFXXX"], payee.CreditorAgentsSeen);
        Assert.Equal([CreditorIban], payee.IbansSeen);
        Assert.Equal(["SHOP WITH AGENT"], payee.NormalizedNamesSeen);
    }

    /// <summary>
    /// Finding 1's exact repro: a blank creditor name with a real IBAN. Before the fix,
    /// <c>IngestionRunner.UpsertPayeesAsync</c> normalized <c>payeeDto.Name</c> — which
    /// <c>TransactionMapper.BuildPayee</c> had already substituted with the raw IBAN as a display
    /// fallback — instead of the (absent) raw creditor name, so <c>NormalizedNamesSeen</c> picked up
    /// a meaningless digit-stripped fragment of the IBAN (e.g. <c>["DE"]</c> for a German IBAN)
    /// rather than staying empty.
    /// </summary>
    [Fact]
    public async Task Ingestion_tick_with_blank_creditor_name_and_a_real_iban_does_not_mangle_the_display_fallback_into_NormalizedNamesSeen()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIbanNoName("2026-01-05", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        Assert.Equal(CreditorIban, payee.Name); // BuildPayee's own display fallback — unrelated to the sets
        Assert.Equal([CreditorIban], payee.IbansSeen);
        Assert.Empty(payee.NormalizedNamesSeen); // pre-fix: ["DE"] — the IBAN's letters, digits stripped
    }

    /// <summary>
    /// Finding 2's whole point: proves the <c>DbUpdateConcurrencyException</c> retry preserves both
    /// writers' <c>RecordSeen</c> additions rather than the loser's silently disappearing under
    /// last-write-wins.
    ///
    /// Modelled by pre-loading the payee into a context (<c>dbA</c>) before a second writer — a
    /// full ingestion tick, its own fresh context via the factory — updates and saves the same row
    /// first, bumping <see cref="Payee.Version"/> underneath dbA's now-stale tracked copy. Handing
    /// that same stale <c>dbA</c> straight to <see cref="RulesEndpoint.HandleAsync"/> (which takes a
    /// db context as a parameter, unlike <c>IngestionRunner</c>, which creates its own per tick)
    /// drives the real retry code inside <c>HandleAsync</c> itself, not just EF Core's own
    /// concurrency mechanics.
    /// </summary>
    [Fact]
    public async Task Concurrent_update_conflict_retries_and_preserves_both_writers_RecordSeen_additions()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var consent = NewConsentStore(clock);

        // Seed the row (an ordinary first tick) so both writers below are updating an
        // already-existing payee, not racing to insert it — that is the PK-violation race the
        // catches next to this one already cover, a different problem from Finding 2's.
        await NewRunner(dbName, consent, new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-01", "Seed Name", CreditorIban))), clock)
            .RunOnceAsync(CancellationToken.None);

        // Writer A loads the row now, before writer B updates it below — this tracked copy is what
        // goes stale.
        await using var dbA = NewInMemoryContext(dbName);
        _ = await dbA.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        // Writer B: a second ingestion tick, its own fresh context from the factory (unrelated to
        // dbA), sees the current row and saves cleanly — bumping Payee.Version underneath dbA.
        await NewRunner(
                dbName, consent,
                new FakeDebitsFetcher(Mapped(BookedWithIban("2026-01-02", "Concurrent From Bank", CreditorIban))),
                clock)
            .RunOnceAsync(CancellationToken.None);

        // Writer A: POST /rules against the now-stale dbA. HandleAsync's own SaveChangesAsync hits
        // the stale Version, catches DbUpdateConcurrencyException, clears dbA's tracker, re-reads
        // the now-current row (writer B's spelling already on it) and reapplies writer A's own data
        // on top — the retry path under test.
        await RulesEndpoint.HandleAsync(
            RuleFor("Concurrent From App", "CA"), dbA, clock, NullLoggerFactory.Instance, CancellationToken.None);

        await using var verify = NewInMemoryContext(dbName);
        var payee = await verify.Payees.SingleAsync(p => p.Id == "iban:" + CreditorIban);

        // Both writers' contributions present, in the order each was recorded — proof the retry
        // re-read writer B's already-committed spelling rather than clobbering it with writer A's.
        Assert.Equal(["SEED NAME", "CONCURRENT FROM BANK", "CONCURRENT FROM APP"], payee.NormalizedNamesSeen);
        Assert.Equal([CreditorIban], payee.IbansSeen);
    }
}
