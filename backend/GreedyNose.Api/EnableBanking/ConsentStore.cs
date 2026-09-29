using System.Text.Json;

namespace GreedyNose.Api.EnableBanking;

/// <summary>A connected account, as far as the tracer bullet cares.</summary>
public sealed record ConnectedAccount(
    string Uid,
    string? Iban,
    string? Name,
    string? Currency,
    string? IdentificationHash)
{
    /// <summary>
    /// What "connected account" means in R10b's identifier — and it is deliberately **not**
    /// <see cref="Uid"/>. Observed 2026-08-18: consenting three times to the same account returned
    /// three different uids (<c>e6b83c96…</c>, <c>280cfc98…</c>, <c>4a96fa90…</c>) for the same
    /// IBAN. The docs say so outright — the uid "is valid only until the session to which the
    /// account belongs is in the AUTHORIZED status" — so this was findable before it was found.
    ///
    /// Keying debits on it would give every debit a new identity after every reconnect, and R10b
    /// would then read the user's entire history as new and alert on all of it — the exact storm
    /// R20's reconnect mode exists to prevent.
    ///
    /// The IBAN is what survives, and it is not a guess: Enable Banking's own
    /// <c>identification_hash</c> — documented for "matching accounts between multiple sessions" —
    /// decodes to a hash over exactly <c>(account.account_id.iban, account.currency)</c>. Using
    /// the IBAN directly is the same identity, minus 130 characters of opacity in every debit id.
    ///
    /// The hash is the fallback rather than the primary because it is the only one of the two that
    /// exists for an account with no IBAN. The uid is last and is wrong in the way described
    /// above; reaching it at all is a step 6 problem.
    /// </summary>
    public string Key => (Iban, IdentificationHash) switch
    {
        ({ } iban, _) when !string.IsNullOrWhiteSpace(iban) => iban.Replace(" ", "").ToUpperInvariant(),
        (_, { } hash) when !string.IsNullOrWhiteSpace(hash) => hash,
        _ => Uid,
    };
}

/// <summary>What survives a restart. The session id is the whole point; the rest is context.</summary>
public sealed record ConsentSnapshot(
    string SessionId,
    string? AspspName,
    IReadOnlyList<ConnectedAccount> Accounts,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Holds the one live consent (TRACER-BULLET.md, step 2), and writes it to a local file so a
/// backend restart does not cost a browser click.
///
/// Step 2 chose memory-only on the argument that "a restart costs one sandbox consent, which is
/// cheap". That held while the backend was being written; it stopped holding once every mapping
/// tweak in steps 4–7 meant clicking through a consent page again. The session itself never died —
/// it lives at Enable Banking, and only our memory of the id was lost — so this recovers what was
/// already there rather than creating new state.
///
/// **Still not the database.** One consent, one file, no users, no history; the file is a
/// development convenience with a credential in it, which is why it is gitignored. R18's data
/// lifecycle and the real persistence remain out of the bullet's scope.
///
/// The pending state is the `state` parameter we sent to Enable Banking: the callback must
/// present the same value back, or it is not the redirect we started. It is deliberately *not*
/// persisted — an authorization interrupted by a restart should fail, not resume.
/// </summary>
public sealed class ConsentStore(string filePath, TimeSpan consentValidity, ILogger<ConsentStore> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock _gate = new();

    private string? _pendingState;

    public string? SessionId { get; private set; }
    public string? AspspName { get; private set; }
    public IReadOnlyList<ConnectedAccount> Accounts { get; private set; } = [];
    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>
    /// When the bank says this consent dies — <c>access.valid_until</c> from the session response,
    /// not the 90 days we asked for. The two are not the same number: ASPSPs cap consent validity
    /// at their own maximum, so the granted window can be shorter than the requested one, and
    /// judging expiry by the request would leave a dead session reading as connected.
    /// Null when the bank did not say.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>The account the rest of the bullet reads. v1 connects one account at a time (R22a).</summary>
    public ConnectedAccount? PrimaryAccount => Accounts.Count > 0 ? Accounts[0] : null;

    /// <summary>
    /// The session and the account it belongs to, read together under <c>_gate</c> — the same lock
    /// <see cref="Complete"/> writes both under. The plain getters above read without it, so a
    /// <c>/callback</c> landing mid-read could pair the old account with the new session. Null when
    /// not connected.
    /// </summary>
    public (string SessionId, ConnectedAccount PrimaryAccount)? Current
    {
        get
        {
            lock (_gate)
            {
                return SessionId is { } sessionId && PrimaryAccount is { } account
                    ? (sessionId, account)
                    : null;
            }
        }
    }

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

    public void Complete(
        string sessionId,
        IReadOnlyList<ConnectedAccount> accounts,
        DateTimeOffset now,
        DateTimeOffset? expiresAt)
    {
        lock (_gate)
        {
            SessionId = sessionId;
            Accounts = accounts;
            ConnectedAt = now;
            ExpiresAt = expiresAt;
            _pendingState = null;

            Save(new ConsentSnapshot(sessionId, AspspName, accounts, now, expiresAt));
        }
    }

    /// <summary>
    /// Reads back a consent left by an earlier run. Call once at startup.
    ///
    /// An expired consent is dropped rather than offered: the bank would reject it anyway, and a
    /// stale session id turns every later call into a confusing 401 instead of an honest "not
    /// connected". Expiry is the bank's own <c>valid_until</c> where it gave one, and only falls
    /// back to the validity we requested where it did not — the requested window can be longer
    /// than the granted one, which would keep a dead session alive here.
    /// </summary>
    public void Restore(DateTimeOffset now)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ConsentSnapshot>(File.ReadAllText(filePath), Json);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionId))
            {
                return;
            }

            var expired = snapshot.ExpiresAt is { } expiresAt
                ? now >= expiresAt
                : now - snapshot.ConnectedAt > consentValidity;

            if (expired)
            {
                logger.LogInformation(
                    "Stored consent from {ConnectedAt} has expired ({ExpiresAt}); ignoring it.",
                    snapshot.ConnectedAt, snapshot.ExpiresAt);

                return;
            }

            lock (_gate)
            {
                SessionId = snapshot.SessionId;
                AspspName = snapshot.AspspName;
                Accounts = snapshot.Accounts;
                ConnectedAt = snapshot.ConnectedAt;
                ExpiresAt = snapshot.ExpiresAt;
            }

            logger.LogInformation("Restored the consent for {Aspsp}, connected {ConnectedAt}.",
                snapshot.AspspName, snapshot.ConnectedAt);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // An unreadable file is not worth failing startup over: /connect rebuilds it, and the
            // cost of being wrong here is one browser click — the same cost we had before.
            logger.LogWarning(exception, "Could not read the stored consent at {Path}; starting disconnected.", filePath);
        }
    }

    private void Save(ConsentSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(snapshot, Json));
        }
        catch (IOException exception)
        {
            // The consent is live in memory either way; only the restart shortcut is lost.
            logger.LogWarning(exception, "Could not store the consent at {Path}.", filePath);
        }
    }
}
