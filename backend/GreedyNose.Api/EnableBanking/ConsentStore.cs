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

public enum ConsentStatus
{
    None,
    Active,
    Expired,
}

/// <summary>
/// A consistent read of one bank's consent (see <see cref="ConsentStore.GetStates"/>). Session and account are
/// set unless <see cref="Status"/> is <see cref="ConsentStatus.None"/>; <see cref="ValidUntil"/> is when
/// the consent dies (or died), null when neither the bank nor our own validity gave one.
/// </summary>
public sealed record ConsentState(
    ConsentStatus Status,
    string? SessionId,
    ConnectedAccount? Account,
    DateTimeOffset? ValidUntil,
    Guid UserId = default,
    string? AspspName = null,
    DateTimeOffset? ConnectedAt = null)
{
    public static readonly ConsentState None = new(ConsentStatus.None, null, null, null);
}

/// <summary>What survives a restart. The session id is the whole point; the rest is context.</summary>
public sealed record ConsentSnapshot(
    Guid UserId,
    string SessionId,
    string? AspspName,
    IReadOnlyList<ConnectedAccount> Accounts,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Holds the live consents in memory — one per <c>(UserId, AspspName)</c>, so several banks at once
/// (Story 3.2) — and persists them through <see cref="IConsentPersistence"/> (the encrypted
/// <c>BankConsents</c> table, Story 1.7), so a backend restart does not cost a browser click and no
/// consent sits in a loose file. The worker walks <see cref="GetStates"/>; the single-consent members
/// (<see cref="SessionId"/>, <see cref="AspspName"/>, ...) read the most recently connected bank's
/// consent and only serve callers that need "the latest" (the health page).
///
/// The session itself never dies with a restart (it lives at Enable Banking; only our memory of the
/// id would be lost), so restoring recovers what was already there rather than creating new state.
///
/// The authorization in progress (the `state` we sent to Enable Banking) is not kept here: since
/// Story 1.4 it is a hashed, expiring, single-use row in the database (<c>PendingConsent</c>).
/// </summary>
public sealed class ConsentStore(IConsentPersistence persistence, TimeSpan consentValidity, ILogger<ConsentStore> logger)
{
    private readonly Lock _gate = new();

    // Serializes completes, so the order in the database and the order in memory cannot diverge.
    private readonly SemaphoreSlim _completeGate = new(1, 1);

    // Guarded by _gate. Replaced as a whole on restore, upserted per bank on complete.
    private Dictionary<(Guid UserId, string AspspName), ConsentSnapshot> _consents = [];

    private ConsentSnapshot? Newest()
    {
        lock (_gate)
        {
            return _consents.Values
                .OrderByDescending(c => c.ConnectedAt)
                .ThenBy(c => c.AspspName, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    /// <summary>The user of the most recently connected consent (Story 3.1). Empty until a consent exists.</summary>
    public Guid UserId => Newest()?.UserId ?? default;
    public string? SessionId => Newest()?.SessionId;
    public string? AspspName => Newest()?.AspspName;
    public IReadOnlyList<ConnectedAccount> Accounts => Newest()?.Accounts ?? [];
    public DateTimeOffset? ConnectedAt => Newest()?.ConnectedAt;

    /// <summary>
    /// When the bank says the newest consent dies — <c>access.valid_until</c> from the session response,
    /// not the 90 days we asked for. The two are not the same number: ASPSPs cap consent validity
    /// at their own maximum, so the granted window can be shorter than the requested one, and
    /// judging expiry by the request would leave a dead session reading as connected.
    /// Null when the bank did not say.
    /// </summary>
    public DateTimeOffset? ExpiresAt => Newest()?.ExpiresAt;

    /// <summary>The account of the newest consent. v1 connects one account per bank (R22a).</summary>
    public ConnectedAccount? PrimaryAccount => Accounts.Count > 0 ? Accounts[0] : null;

    /// <summary>
    /// Where every bank's consent stands right now, oldest connection first, read under <c>_gate</c> — the
    /// same lock <see cref="CompleteAsync"/> writes under — so a <c>/callback</c> landing mid-read cannot
    /// pair one login's account with another's session. A consent without an account is not listed.
    ///
    /// Expiry is judged here, at call time, not once at startup: a backend that stays up past
    /// <c>valid_until</c> must notice (R19). The bank's own <c>valid_until</c> wins; the validity we
    /// requested is only the fallback when the bank did not say — the requested window can be longer
    /// than the granted one, which would keep a dead session alive here.
    /// </summary>
    public IReadOnlyList<ConsentState> GetStates(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _consents.Values
                .Where(c => c.Accounts.Count > 0)
                .OrderBy(c => c.ConnectedAt)
                .ThenBy(c => c.AspspName, StringComparer.Ordinal)
                .Select(c => ToState(c, now))
                .ToList();
        }
    }

    /// <summary>
    /// The session id currently held for ONE bank of one user, null when there is none (Story 4.3: read before a
    /// reconnect replaces it, so the old session can be closed). Other banks are never looked at.
    /// </summary>
    public string? SessionIdFor(Guid userId, string aspspName)
    {
        lock (_gate)
        {
            return _consents.TryGetValue((userId, aspspName), out var snapshot) ? snapshot.SessionId : null;
        }
    }

    /// <summary>
    /// The session id of ONE bank of one user as the database holds it (Story 4.3) — what a reconnect must close,
    /// even when this instance's memory is stale or empty. If the database cannot be read, falls back to memory.
    /// </summary>
    public async Task<string?> GetStoredSessionIdAsync(Guid userId, string aspspName, CancellationToken ct = default)
    {
        try
        {
            return await persistence.LoadSessionIdAsync(userId, aspspName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Reading the stored session of {Aspsp} failed ({ExceptionType}); using this instance's memory.", aspspName, ex.GetType().Name);
            return SessionIdFor(userId, aspspName);
        }
    }

    /// <summary>The newest consent's state, <see cref="ConsentState.None"/> when there is none.</summary>
    public ConsentState GetState(DateTimeOffset now) =>
        GetStates(now).LastOrDefault() ?? ConsentState.None;

    private ConsentState ToState(ConsentSnapshot c, DateTimeOffset now)
    {
        var validUntil = c.ExpiresAt ?? c.ConnectedAt + consentValidity;
        return new ConsentState(
            now >= validUntil ? ConsentStatus.Expired : ConsentStatus.Active,
            c.SessionId,
            c.Accounts[0],
            validUntil,
            c.UserId,
            c.AspspName,
            c.ConnectedAt);
    }

    /// <summary>
    /// Adds or replaces the consent of ONE bank (<c>(userId, aspspName)</c>); other banks are untouched.
    /// <paramref name="aspspName"/> comes from the consent record that was started and verified
    /// (<c>PendingConsent</c>), so the bank name only changes once a consent has actually completed,
    /// never when one is merely started.
    ///
    /// Persists FIRST and updates memory only if that succeeded: a consent that lives only in memory
    /// would silently stop alerts after the next restart, so a failed save throws and leaves the
    /// previous state untouched.
    /// </summary>
    public async Task CompleteAsync(
        Guid userId,
        string sessionId,
        string aspspName,
        IReadOnlyList<ConnectedAccount> accounts,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await _completeGate.WaitAsync(ct);
        try
        {
            var snapshot = new ConsentSnapshot(userId, sessionId, aspspName, accounts, now, expiresAt);
            await persistence.SaveAsync(snapshot, ct);

            lock (_gate)
            {
                _consents[(userId, aspspName)] = snapshot;
            }
        }
        finally
        {
            _completeGate.Release();
        }
    }

    /// <summary>
    /// Replaces the in-memory set with every consent left by earlier runs. Call at startup and before
    /// each tick (the database is the truth).
    ///
    /// An expired consent is loaded like any other and reads back as <see cref="ConsentStatus.Expired"/>
    /// from <see cref="GetStates"/>: dropping it would leave nobody knowing which session died, and
    /// R19's one push would be lost across the restart.
    ///
    /// Nothing is swallowed: a wrong key throws <see cref="ConsentDecryptionException"/> and an
    /// unreachable database throws too, so the backend stops instead of running with a silently empty
    /// consent. An empty table is the normal "not connected yet" and returns quietly.
    /// </summary>
    public async Task RestoreAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // Same gate as CompleteAsync: a /callback landing between the load and the swap would otherwise be
        // overwritten by the older set just read.
        await _completeGate.WaitAsync(ct);
        try
        {
            var snapshots = await persistence.LoadAsync(ct);
            var restored = new Dictionary<(Guid UserId, string AspspName), ConsentSnapshot>();
            foreach (var snapshot in snapshots.Where(s => !string.IsNullOrWhiteSpace(s.SessionId)))
            {
                restored[(snapshot.UserId, snapshot.AspspName ?? "")] = snapshot;
            }

            lock (_gate)
            {
                _consents = restored;
            }
        }
        finally
        {
            _completeGate.Release();
        }

        foreach (var state in GetStates(now))
        {
            logger.LogInformation("Restored the consent for {Aspsp}, connected {ConnectedAt}, state {Status}.",
                state.AspspName, state.ConnectedAt, state.Status);
        }
    }
}
