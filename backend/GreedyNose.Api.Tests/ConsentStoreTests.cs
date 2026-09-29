using GreedyNose.Api.EnableBanking;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

public class ConsentStoreTests
{
    private static ConsentStore NewStore() => new(
        Path.Combine(Path.GetTempPath(), $"consent-store-test-{Guid.NewGuid():N}.json"),
        TimeSpan.FromDays(90),
        NullLogger<ConsentStore>.Instance);

    [Fact]
    public void Current_is_null_when_not_connected()
    {
        Assert.Null(NewStore().Current);
    }

    [Fact]
    public void Current_returns_the_session_and_primary_account_of_the_same_Complete_call()
    {
        var store = NewStore();
        var first = new ConnectedAccount("uid-1", "DE11", null, "EUR", null);
        var second = new ConnectedAccount("uid-2", "DE22", null, "EUR", null);

        store.Complete("session-1", [first], DateTimeOffset.UtcNow, null);
        Assert.Equal(("session-1", first), store.Current);

        store.Complete("session-2", [second], DateTimeOffset.UtcNow, null);
        Assert.Equal(("session-2", second), store.Current);
    }

    [Fact]
    public void Current_is_null_when_the_consent_has_no_accounts()
    {
        var store = NewStore();
        store.Complete("session-1", [], DateTimeOffset.UtcNow, null);

        Assert.Null(store.Current);
    }
}
