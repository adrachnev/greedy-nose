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
/// A consistent read of the consent (see <see cref="ConsentStore.GetState"/>). Session and account are
/// set unless <see cref="Status"/> is <see cref="ConsentStatus.None"/>; <see cref="ValidUntil"/> is when
/// the consent dies (or died), null when neither the bank nor our own validity gave one.
/// </summary>
public sealed record ConsentState(
    ConsentStatus Status,
    string? SessionId,
    ConnectedAccount? Account,
    DateTimeOffset? ValidUntil)
{
    public static readonly ConsentState None = new(ConsentStatus.None, null, null, null);
}

/// <summary>What survives a restart. The session id is the whole point; the rest is context.</summary>
public sealed record ConsentSnapshot(
    string SessionId,
    string? AspspName,
    IReadOnlyList<ConnectedAccount> Accounts,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Holds the one live consent in memory and persists it through <see cref="IConsentPersistence"/>
/// (the encrypted <c>BankConsents</c> table, Story 1.7), so a backend restart does not cost a browser
/// click and no consent sits in a loose file. v1 keeps ONE live consent in memory: the most recently
/// connected bank's. The worker walking several consents is Epic 3.
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
    /// Where the consent stands right now, read under <c>_gate</c> — the same lock
    /// <see cref="CompleteAsync"/> writes session and account under. The plain getters above read without
    /// it, so a <c>/callback</c> landing mid-read could pair the old account with the new session.
    ///
    /// Expiry is judged here, at call time, not once at startup: a backend that stays up past
    /// <c>valid_until</c> must notice (R19). The bank's own <c>valid_until</c> wins; the validity we
    /// requested is only the fallback when the bank did not say — the requested window can be longer
    /// than the granted one, which would keep a dead session alive here.
    /// </summary>
    public ConsentState GetState(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (SessionId is not { } sessionId || PrimaryAccount is not { } account)
            {
                return ConsentState.None;
            }

            var validUntil = ExpiresAt ?? ConnectedAt + consentValidity;

            return new ConsentState(
                validUntil is { } end && now >= end ? ConsentStatus.Expired : ConsentStatus.Active,
                sessionId,
                account,
                validUntil);
        }
    }


    /// <summary>
    /// Replaces the live consent. <paramref name="aspspName"/> comes from the consent record that was
    /// started and verified (<c>PendingConsent</c>), so the bank name only changes once a consent
    /// has actually completed, never when one is merely started.
    ///
    /// Persists FIRST and updates memory only if that succeeded: a consent that lives only in memory
    /// would silently stop alerts after the next restart, so a failed save throws and leaves the
    /// previous state untouched.
    /// </summary>
    public async Task CompleteAsync(
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
            await persistence.SaveAsync(new ConsentSnapshot(sessionId, aspspName, accounts, now, expiresAt), ct);

            lock (_gate)
            {
                SessionId = sessionId;
                AspspName = aspspName;
                Accounts = accounts;
                ConnectedAt = now;
                ExpiresAt = expiresAt;
            }
        }
        finally
        {
            _completeGate.Release();
        }
    }

    /// <summary>
    /// Reads back the consent left by an earlier run (the most recently connected bank's). Call once
    /// at startup.
    ///
    /// An expired consent is loaded like any other and reads back as <see cref="ConsentStatus.Expired"/>
    /// from <see cref="GetState"/>: dropping it would leave nobody knowing which session died, and
    /// R19's one push would be lost across the restart.
    ///
    /// Nothing is swallowed: a wrong key throws <see cref="ConsentDecryptionException"/> and an
    /// unreachable database throws too, so the backend stops instead of running with a silently empty
    /// consent. An empty table is the normal "not connected yet" and returns quietly.
    /// </summary>
    public async Task RestoreAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var snapshots = await persistence.LoadAsync(ct);
        var snapshot = snapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefault();
        if (snapshot is null)
        {
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

        logger.LogInformation("Restored the consent for {Aspsp}, connected {ConnectedAt}, state {Status}.",
            snapshot.AspspName, snapshot.ConnectedAt, GetState(now).Status);
    }
}
