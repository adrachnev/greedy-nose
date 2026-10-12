using System.Globalization;
using GreedyNose.Api.Alerts;
using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Notifications;
using GreedyNose.Api.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// One poll tick (TRACER-02-NOTIFICATIONS.md, step 6): the silent first sync (R25), steady
/// state, or a reconnect after a gap (R20). Fetches, upserts payees, inserts new debits, classifies
/// and — for a bad debit not already logged — sends and logs. A reconnect tick instead sends one
/// summary push (only if any new debit is bad) and commits all new debits with the session hash in
/// one save. Depends only on interfaces and the DB context factory, so it is what
/// <c>IngestionRunnerTests</c> exercises with an in-memory database, a fake <see cref="IDebitsFetcher"/>
/// and a fake <see cref="INotificationSender"/> — no network, no real Postgres.
///
/// Fault isolation is deliberately **not** here: a tick that throws is the timer Function's (<c>IngestionTimerFunction</c>)
/// problem (its per-tick try/catch), so this class can fail loudly and let the caller decide.
/// </summary>
public sealed class IngestionRunner(
    IDbContextFactory<GreedyNoseDbContext> dbContextFactory,
    ConsentStore consent,
    IDebitsFetcher fetcher,
    ISessionStatusChecker sessionStatus,
    INotificationSender sender,
    TimeProvider clock,
    ILogger<IngestionRunner> logger,
    PushFailureWatch? failureWatch = null) : IIngestionTick
{
    /// <summary>
    /// One tick: the ingestion part, then the retry stage for owed pushes (R11a). The owed marks are
    /// read before ingestion, so a mark created by this very tick waits for the next one. The retry
    /// stage runs in a <c>finally</c>: no bank call is needed, so a dead consent, a 401/429 or a failed
    /// fetch must not stop it; the ingestion exception still propagates afterwards.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var owedIds = await ReadOwedIdsAsync();

        try
        {
            await IngestAsync(ct);
        }
        finally
        {
            // Its own time budget, deliberately NOT the tick token: that one is the Function's linked
            // timeout, and a slow bank (several slow HTTP calls) eats it. Owed pushes must not be starved
            // exactly when the bank is slow, or they would be given up silently after 24 h. Each item is
            // an atomic unit, so a stop at the budget's end is safe.
            using var budget = new CancellationTokenSource(RetryBudget);
            await RetryOwedPushesAsync(owedIds, budget.Token);

            // Story 2.10: once per tick, after the retries. Never throws (own budget, own context).
            if (failureWatch is not null)
            {
                await failureWatch.EvaluateAsync();
            }
        }
    }

    /// <summary>Time the retry stage gets per tick, independent of the caller's token.</summary>
    public static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ids of the marks open when the tick begins, of every user, oldest first (so one created by this tick waits for the next).
    /// Not tied to the consent: a mark carries its own user, so retries also run when no consent is loaded.
    /// Can never stop ingestion: a failing read is logged and treated as "nothing owed" for this tick;
    /// it has its own budget too, so an already-cancelled tick token does not matter.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReadOwedIdsAsync()
    {
        try
        {
            using var budget = new CancellationTokenSource(RetryBudget);
            await using var owedDb = await dbContextFactory.CreateDbContextAsync(budget.Token);
            return await owedDb.PendingPushes
                .Where(p => p.EndedAt == null)
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.Id)
                .ToListAsync(budget.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Reading the owed pushes failed ({ExceptionType}); no retries in this tick.", exception.GetType().Name);
            return [];
        }
    }

    private async Task IngestAsync(CancellationToken ct)
    {
        // One consistent read for the whole tick: account and session from the same instant, so a
        // /callback landing mid-tick cannot pair one login's account with another's session.
        var state = consent.GetState(clock.GetUtcNow());
        if (state.Status == ConsentStatus.None)
        {
            logger.LogInformation("No active consent; skipping this tick.");
            return;
        }

        var userId = state.UserId;
        var sessionId = state.SessionId!;
        var account = state.Account!;

        if (state.Status == ConsentStatus.Expired)
        {
            // Time signal (R19): valid_until is reached. No fetch — the bank would only answer 401.
            await NotifyConsentDeadAsync(userId, sessionId, account, "expired", ct);
            return;
        }

        var sessionHash = SessionFingerprint.Of(sessionId);

        // The fetch depends on the mode (a first sync pulls the last FirstSyncWindowDays days), so it is decided before
        // fetching, once, and used for the whole tick. Untracked, so a later ChangeTracker.Clear()
        // cannot affect it; the end-of-tick hash update re-loads its own tracked row.
        AccountSyncState? syncState;
        await using (var modeDb = await dbContextFactory.CreateDbContextAsync(ct))
        {
            syncState = await modeDb.AccountSyncStates.AsNoTracking().SingleOrDefaultAsync(
                s => s.UserId == userId && s.AccountKey == account.Key, ct);
        }

        var mode = DetermineMode(syncState, sessionHash);

        DateTimeOffset? newestDebit = null;
        if (mode != IngestionMode.FirstSync)
        {
            await using var dateDb = await dbContextFactory.CreateDbContextAsync(ct);
            newestDebit = await dateDb.Debits
                .Where(d => d.UserId == userId && d.AccountKey == account.Key)
                .MaxAsync(d => (DateTimeOffset?)d.Timestamp, ct);
        }

        var dateFrom = ComputeDateFrom(
            mode, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), newestDebit, syncState?.FirstSyncCompletedAt);

        MappedDebits mapped;
        try
        {
            mapped = await fetcher.FetchAsync(account, dateFrom, ct);
        }
        catch (EnableBankingRequestException exception) when (exception is { StatusCode: 401, ErrorCode: not null })
        {
            // Bank signal (R19): a 401 that names an error (CLOSED_SESSION, ...) is a candidate only.
            // A 401 without one is our own key ("Wrong signature") and never gets here. The bank must
            // also confirm the session is no longer AUTHORIZED, so a misbehaving 401 cannot cost the
            // user a false "Bank connection expired". Anything else rethrows the original exception.
            var status = await GetSessionStatusOrNullAsync(sessionId, ct);
            if (status is null || string.Equals(status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Fetch answered 401 ({ErrorCode}) but the session status is {Status}; not treating the consent as dead.",
                    exception.ErrorCode, status ?? "unknown");
                throw;
            }

            // The status value is logged on purpose: it is how EXPIRED/REVOKED get discovered.
            logger.LogWarning(
                "Fetch answered 401 ({ErrorCode}) and the session status is {Status}; the bank ended the consent.",
                exception.ErrorCode, status);
            await NotifyConsentDeadAsync(userId, sessionId, account, "closed by the bank", ct);
            return;
        }

        foreach (var skipped in mapped.Skipped)
        {
            logger.LogWarning("Skipped a debit the mapper could not represent — {Detail}", skipped);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();

        await UpsertPayeesAsync(db, userId, mapped.Payload.Payees, mapped.Payload.Debits, now, ct);

        // First sync (R25): this connected account has no AccountSyncState row, i.e. no earlier tick
        // ever finished its first sync. Insert everything as seen, no classification, no
        // notification — R10b's "first sync" consequence, without the onboarding classify screen
        // (deliberately out of scope, doc's "Deliberately out of scope" list). Decided once, from
        // state before this tick's own writes. Not "are there any Debits rows": a first sync cut
        // short after some rows, or an empty account, would then read as steady state.
        //
        // The mode is the one decided before the fetch (`mode`, from the untracked `syncState`); it is
        // not re-read here, so the fetch and the commit path can never disagree.
        var isBootstrap = mode == IngestionMode.FirstSync;

        var isReconnect = mode == IngestionMode.Reconnect;

        var existingIds = await db.Debits
            .Where(d => d.UserId == userId && d.AccountKey == account.Key)
            .Select(d => d.Id)
            .ToListAsync(ct);
        var stored = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(stored, StringComparer.Ordinal);
        var duplicatesInFetch = 0;

        var insertedCount = 0;
        var badCount = 0; // reconnect only: M of R20's summary
        foreach (var dto in mapped.Payload.Debits)
        {
            // Also guards a duplicate id within this one fetch: the reconnect and first-sync ticks
            // commit only at the end, so a second Add of the same key would throw on every retry and
            // the tick could never finish.
            if (!seen.Add(dto.Id))
            {
                if (!stored.Contains(dto.Id))
                {
                    duplicatesInFetch++;
                }

                continue;
            }

            var debit = BuildDebit(userId, dto, account.Key, now);

            if (isBootstrap)
            {
                // Only tracked here, committed below together with the AccountSyncState row.
                db.Debits.Add(debit);
            }
            else if (isReconnect)
            {
                // Classify and count, never send per debit (R20); tracked only, committed below in
                // one save together with the hash. Nothing in this loop clears the ChangeTracker.
                var rule = await db.Rules.SingleOrDefaultAsync(
                    r => r.UserId == userId && r.PayeeId == debit.PayeeId, ct);
                var classification = RuleEngine.Classify(debit.AmountEUR, rule?.Classification, rule?.AmountEUR);
                if (classification.Classification != Classification.Good)
                {
                    badCount++;
                }

                db.Debits.Add(debit);
            }
            else
            {
                // The debit's own insert is deferred inside this call, committed together with its
                // classify/notify outcome — see ClassifyInsertAndNotifyAsync's doc comment for why
                // that has to be one atomic unit, not two separate awaited steps (2026-09-22 second
                // review finding: a cutoff between "committed" and "classified" as two steps could
                // still permanently strand exactly the debit being processed when it struck).
                await ClassifyInsertAndNotifyAsync(db, userId, debit, ct);
            }

            insertedCount++;
        }

        if (duplicatesInFetch > 0)
        {
            logger.LogWarning("Skipped {Count} duplicate debit id(s) within one fetch.", duplicatesInFetch);
        }

        if (isBootstrap)
        {
            // All-or-nothing: every debit of this first sync and the "done" marker land in one
            // SaveChangesAsync (atomic on its own, no explicit transaction), so a tick cut short
            // leaves no marker and the next tick simply runs the whole first sync again — idempotent,
            // because rows already stored are skipped via `seen`. An empty fetch still writes the
            // marker, so the first real charge on an empty account is not swallowed as "history".
            db.AccountSyncStates.Add(new AccountSyncState
            {
                UserId = userId,
                AccountKey = account.Key,
                FirstSyncCompletedAt = now,
                LastSessionIdHash = sessionHash,
            });
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Bootstrap tick for account {AccountKey}: inserted {Count} debits as seen, no notifications.",
                AccountKeyPreview(account.Key), insertedCount);
        }
        else if (isReconnect)
        {
            // R20: one summary push, only when at least one new debit is bad (R20a), sent BEFORE the
            // save. A tick that dies anywhere up to the save leaves no debit and the old hash, so the
            // next tick finds the same new debits and counts the same N/M — a duplicate summary beats
            // a lost one. No NotificationLog row: that log is per debit.
            if (badCount > 0)
            {
                var tokens = await db.DeviceTokens.Where(t => t.UserId == userId).ToListAsync(ct);
                var (title, body) = ReconnectSummary.Build(insertedCount, badCount);
                var sendResult = await SendToAllTokensAsync(db, tokens, title, body, ct);
                await RecordAttemptAsync(userId, sendResult.AnySent, sendResult.Failure);
                if (!sendResult.AnySent)
                {
                    // R11a: the summary is remembered with N and M frozen and retried by the next ticks;
                    // the mark commits in the same save as the debits and the hash.
                    logger.LogWarning(
                        "The reconnect summary reached no device ({Outcome}); marked as owed, committing the debits.",
                        sendResult.Failure);
                    db.PendingPushes.Add(new PendingPush
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        Kind = PendingPushKind.ReconnectSummary,
                        SummaryNewCount = insertedCount,
                        SummaryBadCount = badCount,
                        CreatedAt = clock.GetUtcNow(),
                        Attempts = 1,
                        LastAttemptAt = clock.GetUtcNow(),
                        LastOutcome = sendResult.Failure,
                    });
                }
            }

            logger.LogInformation(
                "Reconnect detected for account {AccountKey}: {NewDebits} new debits, {BadDebits} bad.",
                AccountKeyPreview(account.Key), insertedCount, badCount);

            // Freshly loaded, as in the steady branch below; one save for debits, token prunes and hash.
            var syncRow = await db.AccountSyncStates.SingleAsync(
                s => s.UserId == userId && s.AccountKey == account.Key, ct);
            syncRow.LastSessionIdHash = sessionHash;
            await db.SaveChangesAsync(ct);
        }
        else if (syncState?.LastSessionIdHash != sessionHash)
        {
            // Null hash (silent adoption). Only now, after the whole loop: a tick that
            // dies midway leaves the old hash. A freshly
            // loaded row, not `syncState` — that one is untracked, and the tracker may have been
            // cleared since. No ExecuteUpdateAsync: the tests' in-memory provider does not support it.
            var row = await db.AccountSyncStates.SingleAsync(
                s => s.UserId == userId && s.AccountKey == account.Key, ct);
            row.LastSessionIdHash = sessionHash;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>An unreadable or failing check is "unknown" (null), never "dead"; a caller cancellation still propagates.</summary>
    private async Task<string?> GetSessionStatusOrNullAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await sessionStatus.GetStatusAsync(sessionId, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("The session status check failed ({ExceptionType}); treating it as unknown.", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// R19: the consent is dead (<paramref name="reason"/> says how) — push once per session. Guarded by
    /// <c>AccountSyncState.ExpiryNotifiedSessionHash</c>, which is set only when at least one device took
    /// the push: unlike the debit path, nothing waits behind a retry here (a dead connection has no
    /// debits), and a lost R19 push is the worst silence, so Rejected/Transient/no token leave the guard
    /// empty and the next tick tries again. Send before the one save, so a crash in between costs at most
    /// a duplicate push. No row for the account (the consent died before its first sync finished): log and
    /// return — there is nowhere to keep the guard, and the push would repeat every tick.
    /// </summary>
    private async Task NotifyConsentDeadAsync(Guid userId, string sessionId, ConnectedAccount account, string reason, CancellationToken ct)
    {
        var sessionHash = SessionFingerprint.Of(sessionId);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.AccountSyncStates.SingleOrDefaultAsync(
            s => s.UserId == userId && s.AccountKey == account.Key, ct);
        if (row is null)
        {
            logger.LogWarning(
                "The consent for account {AccountKey} is dead ({Reason}) before its first sync finished; no expiry push (no state row).",
                AccountKeyPreview(account.Key), reason);
            return;
        }

        if (row.ExpiryNotifiedSessionHash == sessionHash)
        {
            logger.LogDebug("The consent for account {AccountKey} is dead ({Reason}); the expiry push was already sent.", AccountKeyPreview(account.Key), reason);
            return;
        }

        var tokens = await db.DeviceTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        if (tokens.Count == 0)
        {
            logger.LogWarning("The consent for account {AccountKey} is dead ({Reason}) but no device token is registered; will retry next tick.", AccountKeyPreview(account.Key), reason);
            return;
        }

        var sendResult = await SendToAllTokensAsync(db, tokens, ConnectionExpiredNotice.Title, ConnectionExpiredNotice.Body, ct);
        if (sendResult.AnySent)
        {
            row.ExpiryNotifiedSessionHash = sessionHash;
            logger.LogInformation("The consent for account {AccountKey} is dead ({Reason}); sent the expiry push.", AccountKeyPreview(account.Key), reason);
        }
        else
        {
            logger.LogWarning("The consent for account {AccountKey} is dead ({Reason}) but the expiry push reached no device; will retry next tick.", AccountKeyPreview(account.Key), reason);
        }

        // One save for the guard and any token prunes. Nothing on this path clears the ChangeTracker.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What this tick is, derived from stored state — never a flag someone has to set and clear.</summary>
    public enum IngestionMode
    {
        FirstSync,
        Steady,
        Reconnect,
    }

    /// <summary>How far back the first sync reads (R25), and the oldest start any tick may use.</summary>
    public const int FirstSyncWindowDays = 90;

    /// <summary>Days re-fetched before the newest known debit; known ids dedupe, so the overlap is harmless.</summary>
    public const int OverlapDays = 7;

    /// <summary>
    /// Where this tick's fetch starts. First sync: <see cref="FirstSyncWindowDays"/> days back. Otherwise the day of the newest
    /// stored debit (else the day the first sync finished) minus <see cref="OverlapDays"/>, never
    /// earlier than <see cref="FirstSyncWindowDays"/> days back. Whole days only: fallback debit ids count per booking day.
    /// </summary>
    public static DateOnly ComputeDateFrom(
        IngestionMode mode, DateOnly today, DateTimeOffset? newestDebit, DateTimeOffset? firstSyncCompletedAt)
    {
        var floor = today.AddDays(-FirstSyncWindowDays);
        var anchor = newestDebit ?? firstSyncCompletedAt;
        if (mode == IngestionMode.FirstSync || anchor is null)
        {
            return floor;
        }

        // A debit booked in the future (value/transaction date fallback) must not push the start
        // past today: the anchor is capped at today before the overlap is subtracted.
        var anchorDay = DateOnly.FromDateTime(anchor.Value.UtcDateTime);
        if (anchorDay > today)
        {
            anchorDay = today;
        }

        var candidate = anchorDay.AddDays(-OverlapDays);
        return candidate < floor ? floor : candidate;
    }

    /// <summary>
    /// No row: first sync (R25). Row with a null hash: written before the column existed, session
    /// unknown — steady state, adopted silently. Same hash: steady. Different hash: the user logged
    /// in at the bank again, a reconnect.
    /// </summary>
    public static IngestionMode DetermineMode(AccountSyncState? state, string sessionHash) => state switch
    {
        null => IngestionMode.FirstSync,
        { LastSessionIdHash: null } => IngestionMode.Steady,
        { LastSessionIdHash: var stored } when stored == sessionHash => IngestionMode.Steady,
        _ => IngestionMode.Reconnect,
    };

    /// <summary>
    /// Upsert-then-overwrite: a payee row may already exist from <c>POST /rules</c> (step 4, a
    /// synthetic "now" <c>FirstSeenAt</c>) or from an earlier tick. Either way this tick's
    /// <c>FirstSeenAt</c> is set to the *earliest* <see cref="DebitDto.Timestamp"/> among this
    /// payee's debits in the current fetch — never <c>now</c> — so it only ever gets more accurate,
    /// never worse (decided 2026-09-22). Name/initials/iban are refreshed the same way step 4's
    /// endpoint refreshes them on every save.
    ///
    /// <c>Payee.RecordSeen</c> is the one field that is <em>not</em> overwritten: ARCHITECTURE.md's
    /// "Payee identity" section asks for a distinct set of every raw IBAN/normalized-name/creditor-
    /// agent ever seen, so those three grow, they never get replaced.
    ///
    /// Races with a concurrent <c>POST /rules</c> insert for a brand-new payee the same way step 4
    /// races with itself: caught on <c>PK_Payees</c>, logged, continue — see
    /// <see cref="RulesEndpoint.HandleAsync"/> for the identical pattern this mirrors. A concurrent
    /// *update* of an already-existing payee (this tick racing a <c>POST /rules</c> save for the
    /// same payee) is a second, independent race — see <see cref="Payee.Version"/> and the
    /// <c>DbUpdateConcurrencyException</c> catch below (2026-09-28 review, Finding 2).
    /// </summary>
    private async Task UpsertPayeesAsync(
        GreedyNoseDbContext db, Guid userId, IReadOnlyList<PayeeDto> payees, IReadOnlyList<DebitDto> debits, DateTimeOffset now, CancellationToken ct)
    {
        var earliestByPayee = debits
            .GroupBy(d => d.PayeeId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Min(d => DateTimeOffset.Parse(d.Timestamp, CultureInfo.InvariantCulture)),
                StringComparer.Ordinal);

        // Local, not inline, because a concurrency conflict below re-runs this exact upsert against
        // freshly re-read rows — RecordSeen and the Name/Initials/Iban/FirstSeenAt refresh are all
        // idempotent against the same payeeDto batch, so applying them twice for an unaffected
        // payee is a harmless no-op, and applying them again for the one payee that actually
        // conflicted is the whole point of the retry.
        async Task ApplyAsync()
        {
            foreach (var payeeDto in payees)
            {
                var earliest = earliestByPayee.TryGetValue(payeeDto.Id, out var value) ? value : now;

                var existing = await db.Payees.SingleOrDefaultAsync(
                    p => p.UserId == userId && p.Id == payeeDto.Id, ct);

                // Same value both payee-upsert paths merge into (RulesEndpoint's own upsert
                // mirrors this) — R3a's normalization of the bank's *raw* creditor name, never
                // payeeDto.Name, which may already be BuildPayee's own display fallback (the raw
                // IBAN, or "Unknown payee") when the bank sent no creditor name at all — see
                // PayeeDto.CreditorName's own doc comment (2026-09-28 review, Finding 1).
                var normalizedName = TransactionMapper.NormalizeName(payeeDto.CreditorName);

                if (existing is null)
                {
                    var created = new Payee
                    {
                        UserId = userId,
                        Id = payeeDto.Id,
                        Name = payeeDto.Name,
                        Initials = payeeDto.Initials,
                        Iban = payeeDto.Iban,
                        FirstSeenAt = earliest,
                    };
                    created.RecordSeen(payeeDto.Iban, normalizedName, payeeDto.CreditorAgent);
                    db.Payees.Add(created);
                }
                else
                {
                    existing.Name = payeeDto.Name;
                    existing.Initials = payeeDto.Initials;
                    existing.Iban = payeeDto.Iban;
                    existing.RecordSeen(payeeDto.Iban, normalizedName, payeeDto.CreditorAgent);
                    if (earliest < existing.FirstSeenAt)
                    {
                        existing.FirstSeenAt = earliest;
                    }
                }
            }
        }

        await ApplyAsync();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An already-existing payee's row moved under us — a concurrent POST /rules updated it
            // (or another tick, though the timer trigger runs ticks strictly sequentially in
            // one process; the same "not this deployment's shape" caveat as the insert race below)
            // between our read above and our write here. Unlike the insert race, last-write-wins
            // would silently drop whichever writer's RecordSeen additions lost, with no exception
            // and no log line — exactly what Payee.Version exists to prevent. One retry: clear the
            // poisoned tracked state, re-read the now-current rows, and reapply this tick's own
            // payee data on top of them. A second conflict inside the same tick is left to
            // propagate — the timer Function's own per-tick try/catch logs it and the next scheduled
            // tick (or the next POST /rules) picks the payee back up, the same self-heals posture
            // as every other race in this method.
            logger.LogInformation("A payee row changed underneath this tick's own upsert (concurrent POST /rules); re-reading and retrying once.");
            db.ChangeTracker.Clear();
            await ApplyAsync();
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: RulesEndpoint.PayeePrimaryKeyName,
                                           })
        {
            logger.LogInformation("A payee was written by a concurrent POST /rules during this tick's upsert; continuing.");
            // The concurrent writer's row already has what it needs; this tick's own Name/Initials/
            // Iban/FirstSeenAt refresh for that one payee is lost for this pass, and picked up again
            // next tick — same "self-heals" posture as RulesEndpoint's own race note.
            //
            // Critical, unlike RulesEndpoint: that endpoint's DbContext is scoped per request and
            // discarded right after, so a poisoned tracked entity dies with it. This context lives
            // for the whole tick. Without clearing it, the failed Payee (and every other payee this
            // batch tried to add or update, since one failure rolls back the whole SaveChangesAsync
            // call) stays tracked as dirty, and the very next SaveChangesAsync on this context — the
            // first debit insert below — fails on the exact same unique violation, uncaught this
            // time, aborting the rest of the tick (2026-09-22 review finding, reproduced).
            db.ChangeTracker.Clear();
        }
    }

    private static Debit BuildDebit(Guid userId, DebitDto dto, string accountKey, DateTimeOffset now) => new()
    {
        UserId = userId,
        Id = dto.Id,
        PayeeId = dto.PayeeId,
        AmountEUR = dto.AmountEUR,
        Timestamp = DateTimeOffset.Parse(dto.Timestamp, CultureInfo.InvariantCulture),
        HasTime = dto.HasTime,
        PaymentType = dto.PaymentType,
        Reference = dto.Reference,
        AccountKey = accountKey,
        FirstSeenAt = now,
    };

    /// <summary>
    /// Classify one new debit and commit it together with the outcome of that classification — the
    /// debit's own insert is deferred until the very end of this method, alongside any
    /// <c>NotificationLog</c> row and any token prunes, all in one <c>SaveChangesAsync</c> call
    /// (2026-09-22, second review finding). An earlier version committed the debit first and
    /// classified it as a separate, later step; a cancellation striking between the two (the
    /// tick's own timeout, or a host shutdown) permanently stranded exactly the debit being
    /// processed at that moment — once a debit exists in <c>Debits</c> it is never looked at
    /// again, and there was no way to tell "inserted, not yet classified" from "inserted and
    /// resolved". Deferring the insert closes that: nothing commits for this debit until its whole
    /// outcome — good, bad-and-sent, or bad-and-not-sent — is known, so a cutoff anywhere in this
    /// method leaves the debit entirely uncommitted, retried next tick exactly like one that was
    /// never fetched at all. The one residual risk is a send that genuinely reached FCM right as
    /// cancellation struck afterward: a possible duplicate push next tick — the already-accepted
    /// direction (send first, log after: a duplicate is tolerable, a missed alert is not).
    /// </summary>
    private async Task ClassifyInsertAndNotifyAsync(GreedyNoseDbContext db, Guid userId, Debit debit, CancellationToken ct)
    {
        var rule = await db.Rules.SingleOrDefaultAsync(
            r => r.UserId == userId && r.PayeeId == debit.PayeeId, ct);

        var classification = RuleEngine.Classify(debit.AmountEUR, rule?.Classification, rule?.AmountEUR);
        db.Debits.Add(debit);

        if (classification.Classification == Classification.Good)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        // R11 guard, defense in depth: under normal operation this debit id cannot already be in
        // NotificationLog — it isn't even inserted yet at this point — so this only matters against
        // a second process concurrently ingesting the same account (not a shape this tracer bullet
        // deploys; the timer trigger runs ticks strictly sequentially in one process). Cheap, and it
        // reads the same table the unique-index catch below guards on the write side.
        var alreadyLogged = await db.NotificationLog.AnyAsync(
            n => n.UserId == userId && n.DebitId == debit.Id, ct);
        if (alreadyLogged)
        {
            await db.SaveChangesAsync(ct); // still commit the debit itself as seen
            return;
        }

        var reason = RuleEngine.DescribeBadReason(classification)!;
        var payee = await db.Payees.SingleAsync(p => p.UserId == userId && p.Id == debit.PayeeId, ct);
        var title = $"{payee.Name} · {RuleEngine.FormatAmountEUR(debit.AmountEUR)}";

        var tokens = await db.DeviceTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        if (tokens.Count == 0)
        {
            // R11a: remembered, not lost — the mark commits with the debit and the retry stage picks it up.
            logger.LogWarning("No device token registered; debit {DebitId} is marked as owed.", DebitIdPreview(debit.Id));
            await RecordAttemptAsync(userId, false, NoTokenOutcome);
            db.PendingPushes.Add(NewDebitMark(userId, debit.Id, reason, NoTokenOutcome));
            await db.SaveChangesAsync(ct);
            return;
        }

        var sendResult = await SendToAllTokensAsync(db, tokens, title, reason, ct);
        await RecordAttemptAsync(userId, sendResult.AnySent, sendResult.Failure);

        if (!sendResult.AnySent)
        {
            logger.LogWarning(
                "The push for debit {DebitId} reached no device ({Outcome}); marked as owed.",
                DebitIdPreview(debit.Id), sendResult.Failure);
            db.PendingPushes.Add(NewDebitMark(userId, debit.Id, reason, sendResult.Failure));
        }
        else
        {
            // One row per debit regardless of how many tokens got Sent — matches the unique index.
            db.NotificationLog.Add(new NotificationLogEntry
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DebitId = debit.Id,
                SentAt = clock.GetUtcNow(),
                Reason = reason,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: "IX_NotificationLog_UserId_DebitId",
                                           })
        {
            // A concurrent tick (a second process, or an overlapping run — the same "not this
            // deployment's shape" caveat as the guard above) already logged this exact debit id.
            // The push we just sent is a harmless duplicate at worst — send-first-log-after's
            // accepted direction. The failed call rolled back everything in this batch (the debit
            // insert, any token prunes) and left it tracked as dirty; clear it — same fix as
            // UpsertPayeesAsync's own race catch — before committing the debit on its own, so the
            // next debit's SaveChangesAsync on this long-lived context isn't poisoned by it.
            logger.LogInformation("NotificationLog row for debit {DebitId} already written by a concurrent tick.", DebitIdPreview(debit.Id));
            db.ChangeTracker.Clear();
            db.Debits.Add(debit);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Story 2.10: one debit or summary push attempt for the failed-in-a-row count. Not used for the expiry push.</summary>
    private Task RecordAttemptAsync(Guid userId, bool success, string outcome) =>
        failureWatch is null ? Task.CompletedTask : failureWatch.RecordAttemptAsync(userId, success, outcome);

    /// <summary>What a send to all tokens came to: whether any took it, and — when none did — which outcomes said no.</summary>
    /// <param name="AnySent">At least one token got <see cref="SendOutcome.Sent"/>.</param>
    /// <param name="Failure">Outcome names of the failing tokens (<c>NoToken</c> for an empty list); empty when sent. Never a token or text.</param>
    private sealed record SendAllResult(bool AnySent, string Failure);

    private const string NoTokenOutcome = "NoToken";

    /// <summary>
    /// Sends one message to every token; reports whether any got <see cref="SendOutcome.Sent"/> and, if not,
    /// the failing outcomes (what a <see cref="PendingPush"/> stores). Only
    /// <see cref="SendOutcome.TokenNoLongerValid"/> prunes (tracked removal, committed by the caller's
    /// next save); <see cref="SendOutcome.Rejected"/> never does and keeps the push owed. Shared by the
    /// per-debit push, the reconnect summary, the retry stage and the expiry push.
    /// </summary>
    private async Task<SendAllResult> SendToAllTokensAsync(
        GreedyNoseDbContext db, IReadOnlyList<DeviceToken> tokens, string title, string body, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return new SendAllResult(false, NoTokenOutcome);
        }

        var anySent = false;
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var result = await sender.SendAsync(new PushMessage(token.Token, title, body), ct);

            switch (result.Outcome)
            {
                case SendOutcome.Sent:
                    anySent = true;
                    break;
                case SendOutcome.TokenNoLongerValid:
                    // The only outcome that may prune, and it prunes just this one token,
                    // independent of how the others answered (decided 2026-09-22).
                    db.DeviceTokens.Remove(token);
                    failures.Add(nameof(SendOutcome.TokenNoLongerValid));
                    logger.LogInformation(
                        "Pruned a token that FCM reported as no longer valid — {TokenPreview}", TokenPreview.Of(token.Token));
                    break;
                case SendOutcome.Rejected:
                case SendOutcome.Transient:
                    // Needs a human, or needs a later retry — either way, never pruned, never logged
                    // as sent here. FirebaseNotificationSender already logged the detail.
                    failures.Add(result.Outcome.ToString());
                    break;
            }
        }

        return new SendAllResult(anySent, anySent ? "" : string.Join(',', failures));
    }

    private PendingPush NewDebitMark(Guid userId, string debitId, string reason, string outcome) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Kind = PendingPushKind.Debit,
        DebitId = debitId,
        Reason = reason,
        CreatedAt = clock.GetUtcNow(),
        Attempts = 1,
        LastAttemptAt = clock.GetUtcNow(),
        LastOutcome = outcome,
    };

    /// <summary>How long a failed push stays owed (R11a); after that it is given up and the debit just stays in the list.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// The retry stage (R11a): every mark that was open when the tick began, oldest first, each in its own
    /// context and save so one failing item never blocks another. Reads stored data only — no bank call —
    /// which is why it also runs when ingestion returned early or threw. Never throws (except a caller's
    /// cancellation): a failing item is logged and stays owed; the 24 h window ends it.
    /// </summary>
    private async Task RetryOwedPushesAsync(IReadOnlyList<Guid> owedIds, CancellationToken ct)
    {
        foreach (var id in owedIds)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await RetryOneAsync(id, ct);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("The retry budget ran out; the remaining owed pushes wait for the next tick.");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Retrying owed push {MarkId} failed ({ExceptionType}); it stays owed.", id, exception.GetType().Name);
            }
        }
    }

    private async Task RetryOneAsync(Guid markId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var mark = await db.PendingPushes.SingleOrDefaultAsync(p => p.Id == markId, ct);
        if (mark is null || mark.EndedAt is not null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var userId = mark.UserId; // the mark knows its user, so the retry needs no consent (Story 3.1)
        var what = mark.Kind == PendingPushKind.Debit ? $"debit {DebitIdPreview(mark.DebitId!)}" : "reconnect summary";

        if (now - mark.CreatedAt > GiveUpAfter)
        {
            mark.EndedAt = now;
            mark.EndReason = PendingPush.EndedGaveUp;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Gave up the owed push for {What} after {Attempts} attempts (last outcome {Outcome}); owed since {Since:o}.",
                what, mark.Attempts, mark.LastOutcome, mark.CreatedAt);
            return;
        }

        string title, body;
        if (mark.Kind == PendingPushKind.Debit)
        {
            if (await db.NotificationLog.AnyAsync(n => n.UserId == userId && n.DebitId == mark.DebitId, ct))
            {
                // R11: already sent once; never twice. Just close the mark.
                mark.EndedAt = now;
                mark.EndReason = PendingPush.EndedSent;
                await db.SaveChangesAsync(ct);
                return;
            }

            var debit = await db.Debits.SingleAsync(d => d.UserId == userId && d.Id == mark.DebitId, ct);
            var payee = await db.Payees.SingleAsync(p => p.UserId == userId && p.Id == debit.PayeeId, ct);
            title = $"{payee.Name} · {RuleEngine.FormatAmountEUR(debit.AmountEUR)}";
            body = mark.Reason!;
        }
        else
        {
            (title, body) = ReconnectSummary.Build(mark.SummaryNewCount!.Value, mark.SummaryBadCount!.Value);
        }

        var tokens = await db.DeviceTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        var result = await SendToAllTokensAsync(db, tokens, title, body, ct);
        await RecordAttemptAsync(userId, result.AnySent, result.Failure);

        mark.Attempts++;
        mark.LastAttemptAt = now;
        mark.LastOutcome = result.AnySent ? nameof(SendOutcome.Sent) : result.Failure;

        if (result.AnySent)
        {
            mark.EndedAt = now;
            mark.EndReason = PendingPush.EndedSent;
            if (mark.Kind == PendingPushKind.Debit)
            {
                db.NotificationLog.Add(new NotificationLogEntry
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DebitId = mark.DebitId!,
                    SentAt = now,
                    Reason = mark.Reason!,
                });
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (result.AnySent && ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: "IX_NotificationLog_UserId_DebitId",
                                           })
        {
            // Someone logged this debit meanwhile (R11): the push just sent is a harmless duplicate;
            // end the mark without a second log row.
            db.ChangeTracker.Clear();
            var again = await db.PendingPushes.SingleAsync(p => p.Id == markId, ct);
            again.EndedAt = now;
            again.EndReason = PendingPush.EndedSent;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Push retry for {What}: attempt {Attempts}, outcome {Outcome}, at {At:o}.",
            what, mark.Attempts, mark.LastOutcome, now);
    }

    private static string DebitIdPreview(string debitId) => debitId.Length > 10 ? $"{debitId[..10]}…" : debitId;

    private static string AccountKeyPreview(string accountKey) => accountKey.Length > 6 ? $"{accountKey[..6]}…" : accountKey;
}
