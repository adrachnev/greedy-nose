namespace GreedyNose.Api.EnableBanking;

/// <summary>A connected account, as far as the tracer bullet cares.</summary>
public sealed record ConnectedAccount(string Uid, string? Iban, string? Name, string? Currency);

/// <summary>
/// Holds the one live consent, **in memory only** (TRACER-BULLET.md, step 2). Restarting the
/// backend costs one sandbox consent, which is cheap; real persistence arrives with the database,
/// which is deliberately out of the bullet's scope.
///
/// The pending state is the `state` parameter we sent to Enable Banking: the callback must
/// present the same value back, or it is not the redirect we started.
/// </summary>
public sealed class ConsentStore
{
    private readonly Lock _gate = new();

    private string? _pendingState;

    public string? SessionId { get; private set; }
    public string? AspspName { get; private set; }
    public IReadOnlyList<ConnectedAccount> Accounts { get; private set; } = [];
    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>The account the rest of the bullet reads. v1 connects one account at a time (R22a).</summary>
    public ConnectedAccount? PrimaryAccount => Accounts.Count > 0 ? Accounts[0] : null;

    public string StartAuthorization(string aspspName)
    {
        lock (_gate)
        {
            _pendingState = Guid.NewGuid().ToString();
            AspspName = aspspName;

            return _pendingState;
        }
    }

    public bool IsExpectedState(string? state)
    {
        lock (_gate)
        {
            return _pendingState is not null && string.Equals(_pendingState, state, StringComparison.Ordinal);
        }
    }

    public void Complete(string sessionId, IReadOnlyList<ConnectedAccount> accounts, DateTimeOffset now)
    {
        lock (_gate)
        {
            SessionId = sessionId;
            Accounts = accounts;
            ConnectedAt = now;
            _pendingState = null;
        }
    }
}
