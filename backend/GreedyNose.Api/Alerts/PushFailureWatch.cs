using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Alerts;

/// <summary>
/// Story 2.10: counts failed push attempts in a row and decides, once per tick, whether the operator mail
/// goes out. Two calls, deliberately separate: <see cref="RecordAttemptAsync"/> after every debit or
/// summary push attempt, <see cref="EvaluateAsync"/> once at the end of the tick.
///
/// Neither ever throws (except a caller's cancellation in Evaluate): the watch is an observer, and a failing
/// counter write or mail must never stop ingestion or the push retry. Each call has its own time budget and
/// its own short database context, so it does not depend on the tick's token or context.
///
/// Incident rules: three failures in a row and no mail yet -> failure mail; the flag is set only after
/// Brevo accepted it, so a failed mail is retried at the next evaluation. A success resets the count; if a
/// failure mail had gone out, one follow-up is owed. A success before any mail closes the incident silently.
/// </summary>
public sealed class PushFailureWatch(
    IDbContextFactory<GreedyNoseDbContext> dbContextFactory,
    IAlertMailSender mail,
    ConsentStore consent,
    TimeProvider clock,
    ILogger<PushFailureWatch> logger)
{
    public const int FailureThreshold = 3;

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <param name="success">At least one device took the push.</param>
    /// <param name="outcome">Failing outcome names (comma list); ignored on success.</param>
    public async Task RecordAttemptAsync(Guid userId, bool success, string outcome)
    {
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            await using var db = await dbContextFactory.CreateDbContextAsync(budget.Token);
            var now = clock.GetUtcNow();
            var row = await db.PushHealth.SingleOrDefaultAsync(h => h.UserId == userId, budget.Token);
            if (row is null)
            {
                row = new PushHealth { UserId = userId };
                db.PushHealth.Add(row);
            }

            if (success)
            {
                row.ConsecutiveFailures = 0;
                if (row.FailureMailSentAt is not null)
                {
                    if (!row.RecoveryMailPending)
                    {
                        row.RecoveryMailPending = true;
                        row.RecoveredAt = now;
                    }
                }
                else
                {
                    // No mail went out: the incident ends without one.
                    row.FirstFailureAt = null;
                    row.LastFailureAt = null;
                    row.LastOutcome = null;
                }
            }
            else
            {
                if (row.ConsecutiveFailures == 0 && row.FailureMailSentAt is null)
                {
                    row.FirstFailureAt = now;
                }

                row.ConsecutiveFailures++;
                row.LastFailureAt = now;
                row.LastOutcome = outcome.Length > 200 ? outcome[..200] : outcome;

                // Failing again before the follow-up went out: same incident, no follow-up yet.
                row.RecoveryMailPending = false;
                row.RecoveredAt = null;
            }

            await db.SaveChangesAsync(budget.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Recording a push attempt failed ({ExceptionType}); the failure count may be off.", exception.GetType().Name);
        }
    }

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
            await EvaluateAllAsync(budget.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Evaluating the push failure watch failed ({ExceptionType}); retried at the next tick.", exception.GetType().Name);
        }
    }

    /// <summary>Every user that has a counter row (Story 3.1: the user comes from the data, not a constant).</summary>
    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        List<Guid> userIds;
        await using (var listDb = await dbContextFactory.CreateDbContextAsync(ct))
        {
            userIds = await listDb.PushHealth.Select(h => h.UserId).ToListAsync(ct);
        }

        foreach (var userId in userIds)
        {
            await EvaluateCoreAsync(userId, ct);
        }
    }

    private async Task EvaluateCoreAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.PushHealth.SingleOrDefaultAsync(h => h.UserId == userId, ct);
        if (row is null)
        {
            return;
        }

        var now = clock.GetUtcNow();

        if (row.ConsecutiveFailures >= FailureThreshold && row.FailureMailSentAt is null)
        {
            var open = await db.PendingPushes
                .Where(p => p.UserId == userId && p.EndedAt == null)
                .Select(p => new { p.Kind, p.SummaryBadCount, p.CreatedAt })
                .ToListAsync(ct);

            var facts = new PushFailureFacts(
                Reasons: string.IsNullOrEmpty(row.LastOutcome) ? "Unknown" : row.LastOutcome,
                ConsecutiveFailures: row.ConsecutiveFailures,
                FirstFailureAt: row.FirstFailureAt ?? now,
                LastFailureAt: row.LastFailureAt ?? now,
                WaitingBadDebits: open.Sum(p => p.Kind == PendingPushKind.Debit ? 1 : p.SummaryBadCount ?? 0),
                OldestWaitingAge: open.Count == 0 ? null : now - open.Min(p => p.CreatedAt),
                Banks: consent.GetStates(now)
                    .Where(s => s.UserId == userId && s.AspspName is { Length: > 0 })
                    .Select(s => s.AspspName!)
                    .ToList());

            var result = await mail.SendAsync(PushFailureMail.BuildFailure(facts), ct);
            if (result.Accepted)
            {
                row.FailureMailSentAt = now;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Sent the push failure mail after {Failures} failed attempts in a row.", row.ConsecutiveFailures);
            }
            else
            {
                logger.LogWarning("The push failure mail was not sent ({Reason}); trying again at the next tick.", result.Reason);
            }

            return;
        }

        if (row.RecoveryMailPending && row.ConsecutiveFailures == 0)
        {
            var since = row.FirstFailureAt ?? now;
            var sent = await db.PendingPushes
                .Where(p => p.UserId == userId && p.EndReason == PendingPush.EndedSent && p.EndedAt >= since)
                .Select(p => new { p.Kind, p.SummaryBadCount })
                .ToListAsync(ct);
            var sentDebits = sent.Sum(p => p.Kind == PendingPushKind.Debit ? 1 : p.SummaryBadCount ?? 0);

            var result = await mail.SendAsync(PushFailureMail.BuildRecovery(sentDebits, since, row.RecoveredAt ?? now), ct);
            if (result.Accepted)
            {
                row.FailureMailSentAt = null;
                row.RecoveryMailPending = false;
                row.RecoveredAt = null;
                row.FirstFailureAt = null;
                row.LastFailureAt = null;
                row.LastOutcome = null;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Sent the push recovery mail.");
            }
            else
            {
                logger.LogWarning("The push recovery mail was not sent ({Reason}); trying again at the next tick.", result.Reason);
            }
        }
    }
}
