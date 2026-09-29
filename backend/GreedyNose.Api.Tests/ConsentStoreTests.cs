using GreedyNose.Api.EnableBanking;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

public class ConsentStoreTests
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(90);
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"consent-store-test-{Guid.NewGuid():N}.json");

    private static ConsentStore NewStore(string? path = null) =>
        new(path ?? TempPath(), Validity, NullLogger<ConsentStore>.Instance);

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
    public void GetState_returns_the_session_and_primary_account_of_the_same_Complete_call()
    {
        var store = NewStore();
        var second = new ConnectedAccount("uid-2", "DE22", null, "EUR", null);

        store.Complete("session-1", [Account], T0, null);
        var first = store.GetState(T0);
        Assert.Equal(ConsentStatus.Active, first.Status);
        Assert.Equal("session-1", first.SessionId);
        Assert.Equal(Account, first.Account);

        store.Complete("session-2", [second], T0, null);
        var next = store.GetState(T0);
        Assert.Equal("session-2", next.SessionId);
        Assert.Equal(second, next.Account);
    }

    [Fact]
    public void GetState_is_None_when_the_consent_has_no_accounts()
    {
        var store = NewStore();
        store.Complete("session-1", [], T0, null);

        Assert.Equal(ConsentStatus.None, store.GetState(T0).Status);
    }

    [Fact]
    public void GetState_is_Active_before_ExpiresAt_and_Expired_exactly_at_it()
    {
        var store = NewStore();
        var expiresAt = T0.AddDays(30);
        store.Complete("session-1", [Account], T0, expiresAt);

        Assert.Equal(ConsentStatus.Active, store.GetState(expiresAt.AddTicks(-1)).Status);

        var atExpiry = store.GetState(expiresAt);
        Assert.Equal(ConsentStatus.Expired, atExpiry.Status);
        Assert.Equal("session-1", atExpiry.SessionId);
        Assert.Equal(Account, atExpiry.Account);
        Assert.Equal(expiresAt, atExpiry.ValidUntil);
    }

    [Fact]
    public void ExpiresAt_wins_over_the_requested_validity_when_the_bank_granted_less()
    {
        var store = NewStore();
        store.Complete("session-1", [Account], T0, T0.AddDays(10));

        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(11)).Status); // well inside the requested 90 days
    }

    [Fact]
    public void Without_ExpiresAt_the_requested_validity_from_ConnectedAt_is_the_fallback()
    {
        var store = NewStore();
        store.Complete("session-1", [Account], T0, null);

        Assert.Equal(ConsentStatus.Active, store.GetState(T0 + Validity - TimeSpan.FromTicks(1)).Status);
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0 + Validity).Status);
    }

    [Fact]
    public void Expiry_is_judged_at_call_time_so_a_long_running_process_notices()
    {
        var store = NewStore();
        store.Complete("session-1", [Account], T0, T0.AddDays(1));

        Assert.Equal(ConsentStatus.Active, store.GetState(T0.AddHours(1)).Status);
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(2)).Status); // same store, clock moved on
    }

    [Fact]
    public void Restore_keeps_an_expired_consent_and_loads_it_as_Expired()
    {
        var path = TempPath();
        NewStore(path).Complete("session-1", [Account], T0, T0.AddDays(1));

        var restored = NewStore(path);
        restored.Restore(T0.AddDays(5));

        var state = restored.GetState(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Expired, state.Status);
        Assert.Equal("session-1", state.SessionId);
        Assert.Equal(Account, state.Account);
    }

    [Fact]
    public void Restore_of_a_live_consent_loads_it_as_Active()
    {
        var path = TempPath();
        NewStore(path).Complete("session-1", [Account], T0, T0.AddDays(30));

        var restored = NewStore(path);
        restored.Restore(T0.AddDays(5));

        Assert.Equal(ConsentStatus.Active, restored.GetState(T0.AddDays(5)).Status);
    }

    [Fact]
    public void Complete_replaces_an_expired_consent_with_an_Active_one()
    {
        var path = TempPath();
        NewStore(path).Complete("session-1", [Account], T0, T0.AddDays(1));
        var store = NewStore(path);
        store.Restore(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Expired, store.GetState(T0.AddDays(5)).Status);

        store.Complete("session-2", [Account], T0.AddDays(5), T0.AddDays(95));

        var state = store.GetState(T0.AddDays(5));
        Assert.Equal(ConsentStatus.Active, state.Status);
        Assert.Equal("session-2", state.SessionId);
    }
}
