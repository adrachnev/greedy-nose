using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

public class ConsentStoreTests
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(90);
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static ConsentStore NewStore(IConsentPersistence? persistence = null) =>
        new(persistence ?? new FakeConsentPersistence(), Validity, NullLogger<ConsentStore>.Instance);

    private static readonly ConnectedAccount Account = new("uid-1", "DE11", null, "EUR", null);

    [Fact]
    public void GetState_is_None_when_not_connected()
    {
        var state = NewStore().GetState(T0);

        Assert.Equal(ConsentStatus.None, state.Status);
        Assert.Null(state.SessionId);
        Assert.Null(state.Account);
    }

    [Fact]
    public async Task GetState_returns_the_session_and_primary_account_of_the_same_Complete_call()
    {
        var store = NewStore();
        var second = new ConnectedAccount("uid-2", "DE22", null, "EUR", null);

        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, null);
        var first = store.GetState(T0);
        Assert.Equal(ConsentStatus.Active, first.Status);
        Assert.Equal("session-1", first.SessionId);
        Assert.Equal(Account, first.Account);

        await store.CompleteAsync(SeedData.UserId, "session-2", "Test Bank", [second], T0, null);
        var next = store.GetState(T0);
        Assert.Equal("session-2", next.SessionId);
        Assert.Equal(second, next.Account);
    }

    [Fact]
    public async Task GetState_is_None_when_the_consent_has_no_accounts()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [], T0, null);

        Assert.Equal(ConsentStatus.None, store.GetState(T0).Status);
    }

    [Fact]
    public async Task GetState_is_Active_before_ExpiresAt_and_Expired_exactly_at_it()
    {
        var store = NewStore();
        var expiresAt = T0.AddDays(30);
        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, expiresAt);

        Assert.Equal(ConsentStatus.Active, store.GetState(expiresAt.AddTicks(-1)).Status);

        var atExpiry = store.GetState(expiresAt);
        Assert.Equal(ConsentStatus.Expired, atExpiry.Status);
        Assert.Equal("session-1", atExpiry.SessionId);
        Assert.Equal(Account, atExpiry.Account);
        Assert.Equal(expiresAt, atExpiry.ValidUntil);
    }

    [Fact]
    public async Task ExpiresAt_wins_over_the_requested_validity_when_the_bank_granted_less()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, T0.AddDays(10));

        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(11)).Status); // well inside the requested 90 days
    }

    [Fact]
    public async Task Without_ExpiresAt_the_requested_validity_from_ConnectedAt_is_the_fallback()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, null);

        Assert.Equal(ConsentStatus.Active, store.GetState(T0 + Validity - TimeSpan.FromTicks(1)).Status);
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0 + Validity).Status);
    }

    [Fact]
    public async Task Expiry_is_judged_at_call_time_so_a_long_running_process_notices()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, T0.AddDays(1));

        Assert.Equal(ConsentStatus.Active, store.GetState(T0.AddHours(1)).Status);
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(2)).Status); // same store, clock moved on
    }

    [Fact]
    public async Task Restore_keeps_an_expired_consent_and_loads_it_as_Expired()
    {
        var persistence = new FakeConsentPersistence();
        await NewStore(persistence).CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, T0.AddDays(1));

        var restored = NewStore(persistence);
        await restored.RestoreAsync(T0.AddDays(5));

        var state = restored.GetState(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Expired, state.Status);
        Assert.Equal("session-1", state.SessionId);
        Assert.Equal(Account, state.Account);
    }

    [Fact]
    public async Task Restore_of_a_live_consent_loads_it_as_Active()
    {
        var persistence = new FakeConsentPersistence();
        await NewStore(persistence).CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, T0.AddDays(30));

        var restored = NewStore(persistence);
        await restored.RestoreAsync(T0.AddDays(5));

        Assert.Equal(ConsentStatus.Active, restored.GetState(T0.AddDays(5)).Status);
    }

    [Fact]
    public async Task Restore_of_an_empty_table_is_quietly_not_connected()
    {
        var store = NewStore();
        await store.RestoreAsync(T0);

        Assert.Equal(ConsentStatus.None, store.GetState(T0).Status);
        Assert.Null(store.SessionId);
    }

    [Fact]
    public async Task Restore_keeps_the_most_recently_connected_bank()
    {
        var persistence = new FakeConsentPersistence();
        var store = NewStore(persistence);
        await store.CompleteAsync(SeedData.UserId, "session-n26", "N26", [Account], T0, null);
        await store.CompleteAsync(SeedData.UserId, "session-dkb", "DKB", [new ConnectedAccount("uid-2", "DE22", null, "EUR", null)], T0.AddDays(1), null);
        await store.CompleteAsync(SeedData.UserId, "session-ing", "ING", [new ConnectedAccount("uid-3", "DE33", null, "EUR", null)], T0.AddHours(1), null);

        var restored = NewStore(persistence);
        await restored.RestoreAsync(T0.AddDays(2));

        Assert.Equal("DKB", restored.AspspName);
        Assert.Equal("session-dkb", restored.SessionId);
    }

    [Fact]
    public async Task A_wrong_key_at_restore_throws_and_never_leaves_a_silently_empty_store()
    {
        var persistence = new FakeConsentPersistence
        {
            LoadFails = new ConsentDecryptionException("could not decrypt"),
        };
        var store = NewStore(persistence);

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => store.RestoreAsync(T0));
    }

    [Fact]
    public async Task Complete_replaces_an_expired_consent_with_an_Active_one()
    {
        var persistence = new FakeConsentPersistence();
        await NewStore(persistence).CompleteAsync(SeedData.UserId, "session-1", "Test Bank", [Account], T0, T0.AddDays(1));
        var store = NewStore(persistence);
        await store.RestoreAsync(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(5)).Status);

        await store.CompleteAsync(SeedData.UserId, "session-2", "Test Bank", [Account], T0.AddDays(5), T0.AddDays(95));

        var state = store.GetState(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Active, state.Status);
        Assert.Equal("session-2", state.SessionId);
    }

    [Fact]
    public async Task Complete_sets_the_bank_name_it_is_given_and_Restore_brings_it_back()
    {
        var persistence = new FakeConsentPersistence();
        var store = NewStore(persistence);
        Assert.Null(store.AspspName);

        await store.CompleteAsync(SeedData.UserId, "session-1", "N26", [Account], T0, null);
        Assert.Equal("N26", store.AspspName);

        var restored = NewStore(persistence);
        await restored.RestoreAsync(T0);
        Assert.Equal("N26", restored.AspspName);
    }

    [Fact]
    public async Task A_failed_save_throws_and_leaves_the_previous_consent_untouched()
    {
        var persistence = new FakeConsentPersistence();
        var store = NewStore(persistence);
        await store.CompleteAsync(SeedData.UserId, "session-1", "N26", [Account], T0, T0.AddDays(30));

        persistence.SaveFails = new InvalidOperationException("db down");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAsync(SeedData.UserId, "session-2", "N26", [Account], T0.AddDays(1), null));

        Assert.Equal("session-1", store.SessionId);
        Assert.Equal(T0, store.ConnectedAt);
        Assert.Equal(T0.AddDays(30), store.ExpiresAt);
    }

    [Fact]
    public async Task A_failed_first_save_leaves_the_store_not_connected()
    {
        var store = NewStore(new FakeConsentPersistence { SaveFails = new InvalidOperationException("db down") });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAsync(SeedData.UserId, "session-1", "N26", [Account], T0, null));

        Assert.Null(store.SessionId);
        Assert.Equal(ConsentStatus.None, store.GetState(T0).Status);
    }

    [Fact]
    public void The_store_has_no_file_system_dependency()
    {
        // The store is built from a persistence seam, a validity and a logger only — no path. (Not
        // asserted via the working directory: it is process-wide and tests run in parallel.)
        var parameters = typeof(ConsentStore).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(string));
        Assert.Null(typeof(EnableBankingOptions).GetProperty("ConsentFilePath"));
    }

    // --- Several banks at once (Story 3.2) ----------------------------------------------------------

    private static readonly ConnectedAccount AccountB = new("uid-2", "DE22", null, "EUR", null);

    [Fact]
    public async Task Restore_keeps_every_bank_and_GetStates_lists_them_oldest_first()
    {
        var persistence = new FakeConsentPersistence();
        var store = NewStore(persistence);
        await store.CompleteAsync(SeedData.UserId, "session-dkb", "DKB", [AccountB], T0.AddDays(1), null);
        await store.CompleteAsync(SeedData.UserId, "session-n26", "N26", [Account], T0, null);

        var restored = NewStore(persistence);
        await restored.RestoreAsync(T0.AddDays(2));

        var states = restored.GetStates(T0.AddDays(2));
        Assert.Equal(["N26", "DKB"], states.Select(s => s.AspspName!).ToArray());
        Assert.Equal(["session-n26", "session-dkb"], states.Select(s => s.SessionId!).ToArray());
        Assert.Equal(T0, states[0].ConnectedAt);
        Assert.Equal("DKB", restored.AspspName); // the single-consent members read the newest
    }

    [Fact]
    public async Task Complete_for_one_bank_replaces_only_that_bank()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "n26-1", "N26", [Account], T0, null);
        await store.CompleteAsync(SeedData.UserId, "dkb-1", "DKB", [AccountB], T0.AddHours(1), null);

        await store.CompleteAsync(SeedData.UserId, "n26-2", "N26", [Account], T0.AddHours(2), null);

        var states = store.GetStates(T0.AddHours(3));
        Assert.Equal(2, states.Count);
        Assert.Equal("dkb-1", states.Single(s => s.AspspName == "DKB").SessionId);
        Assert.Equal("n26-2", states.Single(s => s.AspspName == "N26").SessionId);
    }


    [Fact]
    public async Task Restore_picks_up_a_bank_another_instance_connected()
    {
        var persistence = new FakeConsentPersistence();
        var first = NewStore(persistence);
        await first.CompleteAsync(SeedData.UserId, "n26-1", "N26", [Account], T0, null);
        var second = NewStore(persistence);
        await second.CompleteAsync(SeedData.UserId, "dkb-1", "DKB", [AccountB], T0.AddHours(1), null);
        Assert.Single(first.GetStates(T0));

        await first.RestoreAsync(T0.AddHours(2));

        Assert.Equal(2, first.GetStates(T0.AddHours(2)).Count);
    }
    [Fact]
    public async Task Each_bank_is_judged_expired_on_its_own()
    {
        var store = NewStore();
        await store.CompleteAsync(SeedData.UserId, "n26", "N26", [Account], T0, T0.AddDays(1));
        await store.CompleteAsync(SeedData.UserId, "dkb", "DKB", [AccountB], T0, T0.AddDays(30));

        var states = store.GetStates(T0.AddDays(5));

        Assert.Equal(ConsentStatus.Expired, states.Single(s => s.AspspName == "N26").Status);
        Assert.Equal(ConsentStatus.Active, states.Single(s => s.AspspName == "DKB").Status);
    }

    private sealed class SlowLoadPersistence(FakeConsentPersistence inner) : IConsentPersistence
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default) => inner.SaveAsync(snapshot, ct);

        public async Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default)
        {
            var rows = await inner.LoadAsync(ct); // the older set is read first ...
            LoadStarted.SetResult();
            await Release.Task; // ... and handed over late
            return rows;
        }
    }

    [Fact]
    public async Task A_consent_completed_while_a_restore_is_loading_is_not_overwritten_by_the_older_set()
    {
        var persistence = new SlowLoadPersistence(new FakeConsentPersistence());
        var store = NewStore(persistence);
        await store.CompleteAsync(SeedData.UserId, "n26-1", "N26", [Account], T0, null);

        var restore = store.RestoreAsync(T0);
        await persistence.LoadStarted.Task;
        var complete = store.CompleteAsync(SeedData.UserId, "dkb-1", "DKB", [AccountB], T0.AddHours(1), null);
        persistence.Release.SetResult();
        await Task.WhenAll(restore, complete);

        Assert.Equal(["N26", "DKB"], store.GetStates(T0.AddHours(2)).Select(s => s.AspspName!).ToArray());
    }
}
