using System.Text.Json;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Banks;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 5.5: <c>GET /my-banks</c> and <c>POST /my-banks/first-sync</c>, run directly as handlers against EF Core's
/// in-memory provider and a fake consent persistence. The list handler takes no bank client at all, so it cannot call
/// a bank; the trigger counts the queue requests of the repeat.
/// </summary>
public sealed class MyBanksEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private const string SecretSession = "SENTINEL-SESSION-ID";
    private const string SecretIban = "DE00SENTINELIBAN0000";
    private const string SecretUid = "SENTINEL-UID";

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingTrigger : IFirstSyncTrigger
    {
        public int Requests;
        public Exception? Throws;
        public Action? OnRequest;

        public Task RequestAsync(CancellationToken ct)
        {
            Requests++;
            OnRequest?.Invoke();
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private sealed class TestUser(Guid id) : ICurrentUser
    {
        public Guid UserId => id;
    }

    private readonly FakeClock _clock = new(T0);
    private readonly CountingTrigger _trigger = new();
    private readonly FakeConsentPersistence _persistence = new();
    private readonly ConsentStore _consent;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public MyBanksEndpointTests()
    {
        _consent = new ConsentStore(_persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
    }

    /// <summary>What Postgres does to a timestamptz: it keeps whole microseconds (rounded), the in-memory provider keeps every tick.</summary>
    private sealed class PostgresPrecisionInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<BankConsent>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                if (entry.Entity.FirstSyncRequestedAt is { } at)
                {
                    entry.Entity.FirstSyncRequestedAt = at.AddTicks(-(at.Ticks % 10) + (at.Ticks % 10 >= 5 ? 10 : 0));
                }
            }

            return ValueTask.FromResult(result);
        }
    }

    private DbContextOptionsBuilder<GreedyNoseDbContext> Options() =>
        new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(_dbName).AddInterceptors(new PostgresPrecisionInterceptor());

    private GreedyNoseDbContext NewDb() => new(Options().Options);

    /// <summary>Saves a bank like the consent flow does: the consent in the store and its row (plus the sync row when the first sync is done).</summary>
    private async Task AddBankAsync(
        string name, DateTimeOffset connectedAt, bool synced = false, DateTimeOffset? validUntil = null, string? country = "DE",
        DateTimeOffset? requestedAt = null, DateTimeOffset? lastAttempt = null, string? lastError = null,
        string? accountName = "Owner Name", string? product = null, bool announcedDead = false, Guid? user = null, bool noIban = false)
    {
        var userId = user ?? SeedData.UserId;
        var iban = noIban ? null : SecretIban + name.Length;
        var hash = noIban ? "hash-" + name : null;
        await _consent.CompleteAsync(userId, SecretSession + name, name,
            [new ConnectedAccount(SecretUid, iban, accountName, "EUR", hash, Product: product)], connectedAt, validUntil ?? T0.AddDays(60));
        await using var db = NewDb();
        db.BankConsents.Add(new BankConsent
        {
            UserId = userId,
            AspspName = name,
            SessionIdCipher = [1],
            PayloadCipher = [1],
            UpdatedAt = connectedAt,
            Country = country,
            FirstSyncRequestedAt = requestedAt,
            LastAttemptAt = lastAttempt,
            LastError = lastError,
            ExpiryNotifiedSessionHash = announcedDead ? SessionFingerprint.Of(SecretSession + name) : null,
        });
        if (synced)
        {
            db.AccountSyncStates.Add(new AccountSyncState
            {
                UserId = userId,
                AccountKey = new ConnectedAccount(SecretUid, iban, null, "EUR", hash).Key,
                FirstSyncCompletedAt = connectedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private static (int Status, string Text) Read(IResult result)
    {
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var text = result is IValueHttpResult { Value: not null } v ? JsonSerializer.Serialize(v.Value, JsonSerializerOptions.Web) : "";
        return (status, text);
    }

    private async Task<(int Status, string Text)> ListAsync(Guid? user = null)
    {
        await using var db = NewDb();
        return Read(await MyBanksEndpoint.ListAsync(new TestUser(user ?? SeedData.UserId), _consent, db, _clock, NullLogger.Instance, CancellationToken.None));
    }

    /// <summary>Stands in for losing a race: the in-memory provider never raises a concurrency conflict itself.</summary>
    private sealed class LostRaceInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("lost the race");
    }

    private async Task<(int Status, string Text)> RepeatAsync(
        string? aspsp, Guid? user = null, bool lostRace = false, CancellationToken token = default)
    {
        var options = Options();
        if (lostRace)
        {
            options.AddInterceptors(new LostRaceInterceptor());
        }

        await using var db = new GreedyNoseDbContext(options.Options);
        return Read(await MyBanksEndpoint.FirstSyncAsync(
            new FirstSyncRequest(aspsp), new TestUser(user ?? SeedData.UserId), _consent, db, _trigger, _clock, NullLogger.Instance, token));
    }

    private async Task<JsonElement> BankAsync(string name)
    {
        var (status, text) = await ListAsync();
        Assert.Equal(200, status);
        return JsonDocument.Parse(text).RootElement.GetProperty("banks").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == name).Clone();
    }

    private async Task<(string State, string? Reason)> StateAsync(string name)
    {
        var bank = await BankAsync(name);
        return (bank.GetProperty("state").GetString()!, bank.TryGetProperty("reason", out var r) ? r.GetString() : null);
    }

    private async Task<BankConsent> RowAsync(string name)
    {
        await using var db = NewDb();
        return await db.BankConsents.AsNoTracking().SingleAsync(c => c.AspspName == name);
    }

    // --- the list -----------------------------------------------------------------------------

    [Fact]
    public async Task A_bank_with_a_finished_first_sync_is_connected_even_after_a_failed_poll()
    {
        await AddBankAsync("Alpha", T0.AddDays(-5), synced: true, lastAttempt: T0.AddHours(-1), lastError: "EnableBankingRequestException 429 RATE");

        Assert.Equal(("connected", null), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_bank_whose_first_sync_is_not_finished_and_has_not_failed_is_syncing()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-2), requestedAt: T0.AddMinutes(-2));

        Assert.Equal(("syncing", null), await StateAsync("Alpha"));
    }

    [Theory]
    [InlineData("EnableBankingRequestException 429 RATE_LIMIT", "daily-limit")]
    [InlineData("EnableBankingRequestException 429", "daily-limit")]
    [InlineData("Timeout", "timeout")]
    [InlineData("EnableBankingRequestException 500", "bank-error")]
    [InlineData("HttpRequestException", "bank-error")]
    [InlineData("EnableBankingRequestException 4290", "bank-error")]
    public async Task A_failed_attempt_after_the_request_is_sync_failed_with_a_short_reason(string lastError, string reason)
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-20), requestedAt: T0.AddMinutes(-20), lastAttempt: T0.AddMinutes(-10), lastError: lastError);

        Assert.Equal(("sync_failed", reason), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_failed_attempt_from_before_the_request_does_not_count()
    {
        await AddBankAsync("Alpha", T0.AddHours(-9), requestedAt: T0.AddMinutes(-1), lastAttempt: T0.AddHours(-3), lastError: "Timeout");

        Assert.Equal(("syncing", null), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_queue_message_given_up_is_sync_failed_after_thirty_minutes_not_before()
    {
        await AddBankAsync("Alpha", T0, requestedAt: T0);

        _clock.Now = T0 + MyBanksEndpoint.GiveUpAfter - TimeSpan.FromSeconds(1);
        Assert.Equal(("syncing", null), await StateAsync("Alpha"));

        _clock.Now = T0 + MyBanksEndpoint.GiveUpAfter;
        Assert.Equal(("sync_failed", "not-finished"), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_row_saved_before_the_request_time_existed_counts_from_the_time_of_connection()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-31), requestedAt: null);

        Assert.Equal(("sync_failed", "not-finished"), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_consent_past_its_end_is_expired_and_so_is_one_the_bank_ended()
    {
        await AddBankAsync("Alpha", T0.AddDays(-91), synced: true, validUntil: T0.AddDays(-1));
        await AddBankAsync("Beta", T0.AddDays(-5), synced: true, announcedDead: true);

        Assert.Equal(("expired", null), await StateAsync("Alpha"));
        Assert.Equal(("expired", null), await StateAsync("Beta"));
    }

    [Fact]
    public async Task The_list_has_exactly_the_documented_fields_sorted_by_name_and_nothing_secret()
    {
        await AddBankAsync("beta", T0.AddDays(-5), synced: true, country: "FI", accountName: null, product: "Everyday");
        await AddBankAsync("Alpha", T0.AddDays(-5), synced: true, country: null, accountName: "Main Account");
        await AddBankAsync("Gamma", T0.AddMinutes(-5), requestedAt: T0.AddMinutes(-5), accountName: null);

        var (status, text) = await ListAsync();

        Assert.Equal(200, status);
        var banks = JsonDocument.Parse(text).RootElement.GetProperty("banks").EnumerateArray().ToList();
        Assert.Equal(["Alpha", "beta", "Gamma"], banks.Select(b => b.GetProperty("name").GetString()));
        Assert.All(banks.Where(b => b.GetProperty("state").GetString() != "sync_failed"),
            b => Assert.Equal(["name", "country", "account", "validUntil", "state"], b.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(JsonValueKind.Null, banks[0].GetProperty("country").ValueKind); // an old row: unknown, never guessed
        Assert.Equal("Main Account", banks[0].GetProperty("account").GetString());
        Assert.Equal("FI", banks[1].GetProperty("country").GetString());
        Assert.Equal("Everyday", banks[1].GetProperty("account").GetString()); // the product when there is no name
        Assert.Equal(JsonValueKind.Null, banks[2].GetProperty("account").ValueKind);
        Assert.Equal(T0.AddDays(60), banks[0].GetProperty("validUntil").GetDateTimeOffset());
        foreach (var secret in new[] { SecretSession, SecretIban, SecretUid, "Owner Name" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public async Task Another_users_banks_are_not_listed_and_an_empty_list_is_a_normal_answer()
    {
        await AddBankAsync("Alpha", T0.AddDays(-5), synced: true, user: Guid.NewGuid());

        var (status, text) = await ListAsync();

        Assert.Equal(200, status);
        Assert.Equal("""{"banks":[]}""", text);
    }

    [Fact]
    public async Task Reading_the_list_queues_nothing_and_changes_nothing()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-40), requestedAt: T0.AddMinutes(-40));
        var before = await RowAsync("Alpha");

        await ListAsync();
        await ListAsync();

        Assert.Equal(0, _trigger.Requests);
        var after = await RowAsync("Alpha");
        Assert.Equal(before.FirstSyncRequestedAt, after.FirstSyncRequestedAt);
        Assert.Equal(before.LastAttemptAt, after.LastAttemptAt);
    }

    // --- the repeat ---------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_first_sync_is_queued_again_the_state_reads_syncing_and_no_consent_is_asked()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-20), requestedAt: T0.AddMinutes(-20), lastAttempt: T0.AddMinutes(-10), lastError: "EnableBankingRequestException 500");
        Assert.Equal("sync_failed", (await StateAsync("Alpha")).State);

        var (status, text) = await RepeatAsync("alpha"); // the name is compared ignoring case

        Assert.Equal(200, status);
        Assert.Equal("""{"state":"syncing"}""", text);
        Assert.Equal(1, _trigger.Requests);
        Assert.Equal(T0, (await RowAsync("Alpha")).FirstSyncRequestedAt);
        Assert.Equal(("syncing", null), await StateAsync("Alpha"));
        Assert.Equal(SecretSession + "Alpha", _consent.SessionIdFor(SeedData.UserId, "Alpha"));
    }

    [Fact]
    public async Task A_given_up_queue_message_can_be_repeated()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));

        Assert.Equal(200, (await RepeatAsync("Alpha")).Status);

        Assert.Equal(1, _trigger.Requests);
        Assert.Equal(("syncing", null), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task At_the_daily_limit_the_repeat_is_refused_with_try_again_tomorrow_and_nothing_is_queued()
    {
        await AddBankAsync("Alpha", T0.AddHours(-2), requestedAt: T0.AddHours(-2), lastAttempt: T0.AddHours(-1), lastError: "EnableBankingRequestException 429 RATE");
        var before = await RowAsync("Alpha");

        var (status, text) = await RepeatAsync("Alpha");

        Assert.Equal(409, status);
        Assert.Contains("tomorrow", text);
        Assert.Equal(0, _trigger.Requests);
        Assert.Equal(before.FirstSyncRequestedAt, (await RowAsync("Alpha")).FirstSyncRequestedAt);
        Assert.Equal(("sync_failed", "daily-limit"), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task The_next_utc_day_the_daily_limit_repeat_is_allowed()
    {
        // The failed attempt was late yesterday (UTC); now it is early today.
        _clock.Now = new DateTimeOffset(2026, 10, 23, 0, 5, 0, TimeSpan.Zero);
        await AddBankAsync("Alpha", T0.AddHours(-30), requestedAt: T0.AddHours(-30), lastAttempt: new DateTimeOffset(2026, 10, 22, 23, 55, 0, TimeSpan.Zero), lastError: "EnableBankingRequestException 429");

        Assert.Equal(200, (await RepeatAsync("Alpha")).Status);

        Assert.Equal(1, _trigger.Requests);
    }

    [Theory]
    [InlineData("syncing")]
    [InlineData("connected")]
    [InlineData("expired")]
    public async Task In_any_other_state_the_repeat_is_refused_and_nothing_is_queued(string state)
    {
        switch (state)
        {
            case "syncing": await AddBankAsync("Alpha", T0.AddMinutes(-2), requestedAt: T0.AddMinutes(-2)); break;
            case "connected": await AddBankAsync("Alpha", T0.AddDays(-5), synced: true); break;
            default: await AddBankAsync("Alpha", T0.AddDays(-91), synced: true, validUntil: T0.AddDays(-1)); break;
        }

        var before = await RowAsync("Alpha");
        Assert.Equal(409, (await RepeatAsync("Alpha")).Status);

        Assert.Equal(0, _trigger.Requests);
        Assert.Equal(before.FirstSyncRequestedAt, (await RowAsync("Alpha")).FirstSyncRequestedAt);
    }

    [Theory]
    [InlineData("Nowhere")]
    [InlineData("Al")]
    public async Task An_unknown_bank_is_404_and_another_users_bank_is_too(string name)
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));

        Assert.Equal(404, (await RepeatAsync(name)).Status);
        Assert.Equal(404, (await RepeatAsync("Alpha", user: Guid.NewGuid())).Status);
        Assert.Equal(0, _trigger.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_name_is_400(string? name)
    {
        Assert.Equal(400, (await RepeatAsync(name)).Status);
        Assert.Equal(0, _trigger.Requests);
    }

    [Fact]
    public async Task When_the_queue_refuses_the_answer_is_503_and_the_marker_stays()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));
        _trigger.Throws = new InvalidOperationException("queue down Host=secret");

        var (status, text) = await RepeatAsync("Alpha");

        Assert.Equal(503, status);
        Assert.DoesNotContain("secret", text);
        // The marker is given back: the bank reads sync_failed again and an immediate retry is accepted.
        Assert.Equal(T0.AddMinutes(-45), (await RowAsync("Alpha")).FirstSyncRequestedAt);
        Assert.Equal(("sync_failed", "not-finished"), await StateAsync("Alpha"));
        _trigger.Throws = null;
        Assert.Equal(200, (await RepeatAsync("Alpha")).Status);
        Assert.Equal(("syncing", null), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task The_marker_is_cut_to_whole_microseconds_so_it_can_be_compared_with_what_the_database_keeps()
    {
        _clock.Now = T0.AddTicks(7); // a real clock has 100 ns ticks, Postgres keeps microseconds
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));

        Assert.Equal(200, (await RepeatAsync("Alpha")).Status);

        var marker = (await RowAsync("Alpha")).FirstSyncRequestedAt!.Value;
        Assert.Equal(0, marker.Ticks % 10);
        Assert.Equal(T0, marker);
    }

    [Fact]
    public async Task The_restore_after_a_queue_refusal_works_with_sub_microsecond_ticks_on_a_database_that_rounds()
    {
        _clock.Now = T0.AddTicks(7);
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));
        _trigger.Throws = new InvalidOperationException("queue down");

        Assert.Equal(503, (await RepeatAsync("Alpha")).Status);

        Assert.Equal(T0.AddMinutes(-45), (await RowAsync("Alpha")).FirstSyncRequestedAt);
        Assert.Equal(("sync_failed", "not-finished"), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task A_caller_that_hangs_up_during_the_queue_call_gets_the_marker_back_and_the_cancellation_goes_on()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));
        using var hangUp = new CancellationTokenSource();
        _trigger.OnRequest = () => hangUp.Cancel();
        _trigger.Throws = new OperationCanceledException(hangUp.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepeatAsync("Alpha", token: hangUp.Token));

        Assert.Equal(1, _trigger.Requests);
        Assert.Equal(T0.AddMinutes(-45), (await RowAsync("Alpha")).FirstSyncRequestedAt);
        Assert.Equal(("sync_failed", "not-finished"), await StateAsync("Alpha"));
    }

    [Fact]
    public async Task Two_repeats_at_once_only_one_queues_the_loser_gets_409_and_changes_nothing()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-45), requestedAt: T0.AddMinutes(-45));

        // The loser read the same marker; its conditional write finds it changed (simulated like the other race tests).
        var (status, text) = await RepeatAsync("Alpha", lostRace: true);

        Assert.Equal(409, status);
        Assert.Contains("already", text);
        Assert.Equal(0, _trigger.Requests);
        Assert.Equal(T0.AddMinutes(-45), (await RowAsync("Alpha")).FirstSyncRequestedAt);
    }

    [Fact]
    public void The_marker_is_a_concurrency_token_of_the_model()
    {
        using var db = NewDb();
        var property = db.Model.FindEntityType(typeof(BankConsent))!.FindProperty(nameof(BankConsent.FirstSyncRequestedAt))!;
        Assert.True(property.IsConcurrencyToken);
    }

    [Fact]
    public async Task An_account_without_an_iban_is_judged_by_its_identification_hash_before_and_after_a_reconnect()
    {
        await AddBankAsync("Alpha", T0.AddMinutes(-2), requestedAt: T0.AddMinutes(-2), noIban: true);
        Assert.Equal(("syncing", null), await StateAsync("Alpha"));

        await AddBankAsync("Beta", T0.AddDays(-5), synced: true, noIban: true);
        Assert.Equal(("connected", null), await StateAsync("Beta"));

        // A reconnect: a new session, the same account (same hash) -> the finished first sync still counts.
        await _consent.CompleteAsync(SeedData.UserId, "new-session", "Beta",
            [new ConnectedAccount("other-uid", null, "Owner Name", "EUR", "hash-Beta")], T0, T0.AddDays(90));
        Assert.Equal(("connected", null), await StateAsync("Beta"));
    }

    [Fact]
    public async Task A_consent_the_bank_ended_but_not_yet_announced_reads_as_before_until_the_tick_records_it()
    {
        // Q2 / Story 4.2: "dead" is never inferred from LastError; only the announced case reads expired.
        await AddBankAsync("Alpha", T0.AddDays(-5), synced: true, lastAttempt: T0.AddHours(-1), lastError: "EnableBankingRequestException 401 CLOSED_SESSION");

        Assert.Equal(("connected", null), await StateAsync("Alpha"));
    }

    // --- saving writes the country and the request time ------------------------------------------

    private sealed class Factory(string name) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(name).Options);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task Saving_a_bank_writes_its_country_and_the_request_time_and_a_reconnect_keeps_what_it_does_not_know()
    {
        var factory = new Factory(Guid.NewGuid().ToString());
        var persistence = new DbConsentPersistence(factory, new ConsentCipher(new byte[32]));
        var store = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        var account = new ConnectedAccount("u", SecretIban, "Main", "EUR", null);

        await store.CompleteAsync(SeedData.UserId, "s1", "Alpha", [account], T0, T0.AddDays(90), CancellationToken.None, "DE", firstSyncRequestedAt: T0);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.BankConsents.SingleAsync();
            Assert.Equal(("DE", T0), (row.Country, row.FirstSyncRequestedAt));
        }

        await store.CompleteAsync(SeedData.UserId, "s2", "Alpha", [account], T0.AddDays(1), T0.AddDays(90), CancellationToken.None, country: null, firstSyncRequestedAt: T0.AddDays(1));
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.BankConsents.SingleAsync();
            Assert.Equal(("DE", T0.AddDays(1)), (row.Country, row.FirstSyncRequestedAt)); // the country is kept, the time moves on
        }

        await store.CompleteAsync(SeedData.UserId, "s3", "Alpha", [account], T0.AddDays(2), T0.AddDays(90), CancellationToken.None, "FR", T0.AddDays(2));
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("FR", (await db.BankConsents.SingleAsync()).Country);
        }
    }
}
