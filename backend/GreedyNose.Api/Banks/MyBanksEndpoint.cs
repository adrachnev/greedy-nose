using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Status;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Banks;

/// <summary>The body of <c>POST /my-banks/first-sync</c>: the bank's name as <c>GET /my-banks</c> gave it.</summary>
public sealed record FirstSyncRequest(string? Aspsp);

/// <summary>One saved bank as the app shows it (Story 5.5). Nothing else of the consent is in it.</summary>
public sealed record MyBank(
    string Name,
    string? Country,
    string? Account,
    DateTimeOffset? ValidUntil,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

/// <summary>The wire values of <see cref="MyBank.State"/> and of its reason.</summary>
public static class MyBankStates
{
    public const string Connected = "connected";
    public const string Syncing = "syncing";
    public const string SyncFailed = "sync_failed";
    public const string Expired = "expired";

    public const string ReasonDailyLimit = "daily-limit";
    public const string ReasonTimeout = "timeout";
    public const string ReasonBankError = "bank-error";
    public const string ReasonNotFinished = "not-finished";
}

/// <summary>
/// <c>GET /my-banks</c> and <c>POST /my-banks/first-sync</c> (Story 5.5, R25, R29, R36), behind the API key.
/// Contract of the list: one entry per saved bank of the user, sorted by name, with name, country (null when unknown:
/// banks saved before the country was stored), account name (the account's name, else its product, else null — never an
/// IBAN), valid-until, one state and, for sync_failed only, a short reason. It reads the consent store (restored from the
/// database first, like /status) and stored rows only and NEVER calls a bank, so the app may ask every few seconds. It holds
/// no session id, IBAN, token, payee or amount.
///
/// States: expired (the consent ran out, or the bank ended it and the owner was told: needs a reconnect); connected (the
/// first sync is done — a later failed poll is not "sync failed"); otherwise the first sync is not finished and it is
/// sync_failed when the last attempt after the request failed, or when <see cref="GiveUpAfter"/> passed since the request
/// (the queue retries a few minutes, then gives the message up), else syncing.
///
/// The repeat is accepted only in sync_failed (409 otherwise, 404 for an unknown bank, name compared ignoring case) and
/// not on the UTC day of a daily-limit failure (409, nothing queued, no bank call). Accepted: the request time is set (the
/// state reads syncing at once) and the first sync is requested; if the queue refuses, 503 and the time stays.
/// </summary>
public static class MyBanksEndpoint
{
    /// <summary>The first-sync queue message retries up to ~8 minutes three times; after this the message is taken as given up.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan QueueRequestTimeout = TimeSpan.FromSeconds(5);

    private sealed record Entry(MyBank Bank, BankConsent? Row);

    public static async Task<IResult> ListAsync(
        ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            var entries = await ReadAsync(user.UserId, consent, db, clock.GetUtcNow(), ct);
            return Results.Ok(new { banks = entries.Select(e => e.Bank).ToList() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(logger, ex);
        }
    }

    public static async Task<IResult> FirstSyncAsync(
        FirstSyncRequest? request, ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, IFirstSyncTrigger firstSync,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Aspsp) || request.Aspsp.Length > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["aspsp"] = ["The name of one of your banks."] });
        }

        try
        {
            // Whole microseconds, because the marker is written and later compared with what was written (see DbTime).
            var now = clock.GetUtcNow().TruncateToMicroseconds();
            var entries = await ReadAsync(user.UserId, consent, db, now, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Bank.Name, request.Aspsp.Trim(), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return Results.NotFound();
            }

            if (entry.Bank.State != MyBankStates.SyncFailed || entry.Row is null)
            {
                return Results.Problem(
                    "Only a bank whose first sync failed can repeat it.", statusCode: StatusCodes.Status409Conflict, title: "Nothing to repeat");
            }

            if (entry.Bank.Reason == MyBankStates.ReasonDailyLimit && entry.Row.LastAttemptAt?.UtcDateTime.Date == now.UtcDateTime.Date)
            {
                return Results.Problem(
                    "The bank's daily limit is used up. Try again tomorrow.", statusCode: StatusCodes.Status409Conflict, title: "Try again tomorrow");
            }

            // Track the row (the list read it without tracking) and set the marker: from now on the state reads syncing.
            // FirstSyncRequestedAt is a concurrency token, so the write is "where it still has the value I read": of two
            // repeats at once only one wins, the other changes nothing and queues nothing.
            var row = await db.BankConsents.SingleAsync(c => c.UserId == entry.Row.UserId && c.AspspName == entry.Row.AspspName, ct);
            var previous = row.FirstSyncRequestedAt;
            row.FirstSyncRequestedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return Results.Problem(
                    "The first sync is already being repeated.", statusCode: StatusCodes.Status409Conflict, title: "Already repeating");
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(QueueRequestTimeout);
                await firstSync.RequestAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller hung up while the queue was asked: nothing was requested for sure, so give the marker back
                // (with no token, it must not be skipped) and let the cancellation go on.
                await RestoreMarkerAsync(db, entry.Row.UserId, entry.Row.AspspName, now, previous, logger);
                throw;
            }
            catch (Exception ex)
            {
                // Type only: a message can carry a connection string.
                logger.LogWarning("The first sync could not be requested again ({ExceptionType}).", ex.GetType().Name);
                await RestoreMarkerAsync(db, entry.Row.UserId, entry.Row.AspspName, now, previous, logger);
                return Results.Problem(
                    "The first sync could not be queued. Try again in a moment.",
                    statusCode: StatusCodes.Status503ServiceUnavailable, title: "Could not queue the first sync");
            }

            return Results.Ok(new { state = MyBankStates.Syncing });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(logger, ex);
        }
    }

    /// <summary>
    /// The queue refused, so nothing was requested: give the marker its old value back (best effort) so the bank reads
    /// sync_failed again and the owner can try at once, instead of "syncing" for up to 30 minutes with nothing queued.
    /// Only if the marker is still the one this call wrote.
    /// </summary>
    private static async Task RestoreMarkerAsync(
        GreedyNoseDbContext db, Guid userId, string aspspName, DateTimeOffset written, DateTimeOffset? previous, ILogger logger)
    {
        try
        {
            db.ChangeTracker.Clear();
            var row = await db.BankConsents.SingleOrDefaultAsync(c => c.UserId == userId && c.AspspName == aspspName);
            if (row is not null && row.FirstSyncRequestedAt == written)
            {
                row.FirstSyncRequestedAt = previous;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("The first sync marker could not be restored ({ExceptionType}); it reads syncing until the give-up time.", ex.GetType().Name);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<List<Entry>> ReadAsync(
        Guid userId, ConsentStore consent, GreedyNoseDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await consent.RestoreAsync(now, ct);
        var basics = await ConnectionStatusReader.ReadAsync(consent, db, userId, now, ct);
        var states = consent.GetStates(now).Where(s => s.UserId == userId).ToList();
        var rows = await db.BankConsents.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct);

        var result = new List<Entry>();
        foreach (var s in states)
        {
            var row = rows.FirstOrDefault(r => r.AspspName == (s.AspspName ?? ""));
            var basic = basics.FirstOrDefault(b => b.Aspsp == s.AspspName);
            var (state, reason) = Judge(basic?.State, s, row, now);
            result.Add(new Entry(
                new MyBank(s.AspspName ?? "", row?.Country, s.Account is null ? null : ConnectChoice.AccountName(s.Account), s.ValidUntil, state, reason),
                row));
        }

        return [.. result.OrderBy(e => e.Bank.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The state the app sees, from the reader's judgement (expired / dead / first sync pending / connected) and the poll bookkeeping.</summary>
    private static (string State, string? Reason) Judge(string? basic, ConsentState consent, BankConsent? row, DateTimeOffset now)
    {
        switch (basic)
        {
            case ConnectionStates.Expired or ConnectionStates.Dead:
                return (MyBankStates.Expired, null);
            case ConnectionStates.FirstSyncPending:
                break;
            default:
                return (MyBankStates.Connected, null);
        }

        var requestedAt = row?.FirstSyncRequestedAt ?? consent.ConnectedAt ?? now;
        // LastAttemptAt is the START time of the tick (not its end), so a failure of a tick that started before the request
        // is not shown as this request's failure: it is ignored, and the 30 minute rule below catches a request that never finished.
        if (row?.LastError is { Length: > 0 } error && row.LastAttemptAt >= requestedAt)
        {
            return (MyBankStates.SyncFailed, ReasonOf(error));
        }

        return now - requestedAt >= GiveUpAfter
            ? (MyBankStates.SyncFailed, MyBankStates.ReasonNotFinished)
            : (MyBankStates.Syncing, null);
    }

    // LastError is "<ExceptionType> <status> <code>" for a bank answer (429 = the daily limit), "Timeout", or a type name.
    private static string ReasonOf(string lastError) =>
        Regex.IsMatch(lastError, @"(^|\s)429(\s|$)") ? MyBankStates.ReasonDailyLimit
        : lastError.StartsWith("Timeout", StringComparison.Ordinal) ? MyBankStates.ReasonTimeout
        : MyBankStates.ReasonBankError;

    private static IResult Unavailable(ILogger logger, Exception ex)
    {
        // Type only: a message can carry connection details.
        logger.LogError("Reading or repeating the first sync failed ({ExceptionType}).", ex.GetType().Name);
        return Results.Problem(title: "Your banks are unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
