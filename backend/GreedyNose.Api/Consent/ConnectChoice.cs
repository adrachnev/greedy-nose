using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Consent;

/// <summary>The body of <c>POST /connect-attempts/{id}/choice</c>: the id of a row of the list.</summary>
public sealed record ChoiceRequest(string? AccountId);

/// <summary>One row of the list the owner picks from; nothing else of the bank's answer is in it.</summary>
public sealed record AccountRow(string Id, string Name, string Kind, string? Last4, bool Suggested);

/// <summary>
/// Choosing the account to watch when a bank offered several in euro (Story 5.4). Three calls on an attempt in the
/// state "choose account", all behind the API key:
/// <list type="bullet">
/// <item><c>GET /connect-attempts/{id}/accounts</c> — the rows to pick from.</item>
/// <item><c>POST /connect-attempts/{id}/choice</c> — saves the bank with the chosen account.</item>
/// <item><c>POST /connect-attempts/{id}/cancel</c> — leaves the choice: the waiting session is closed, nothing is saved.</item>
/// </list>
/// Contract: a malformed, unknown or foreign attempt id is 404. An attempt that is not (any more) in the state "choose
/// account" — waiting, saved, failed, expired, a second choice — is 409, after the cleanup of Story 5.3 ran for an
/// expired one. A choice whose accountId is not in the list is 400. A refusal changes nothing. No answer holds a session
/// id, uid or full IBAN, and no log line holds an account name, IBAN, uid or session id; the rows of the list are the one
/// place an account name and the last four digits appear, in the authenticated answer.
///
/// Two calls at once cannot both win: each starts by one write guarded by the row's <see cref="PendingConsent.Version"/>,
/// and the loser is refused with 409.
/// </summary>
public static class ConnectChoice
{
    private static readonly string Category = typeof(ConnectChoice).FullName!;
    private const int MaxNameLength = 100;
    private const string CurrentAccountType = "CACC";

    // --- GET /connect-attempts/{id}/accounts ---------------------------------------------------

    public static async Task<IResult> AccountsAsync(
        string? id, ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(Category);
        try
        {
            var (row, refusal) = await LoadLiveAsync(id, user, db, eb, cipher, clock.GetUtcNow(), logger, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            return Results.Ok(new { accounts = ToRows(WaitingPayload.Open(cipher, row!.Id, row.WaitingPayload!).Accounts) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(logger, ex);
        }
    }

    /// <summary>
    /// The rows: id (the 1-based position in the stored list, as text — stable because the payload does not change),
    /// name (the account's name, else its product, else "Account"), kind, the last four characters of the IBAN, and
    /// "suggested" on the first current account (a hint only). Nothing else of the bank's answer. An account without an
    /// IBAN is listed (last4 null) and can be chosen: it is then kept under <see cref="ConnectedAccount.Key"/>, which falls
    /// back to the bank's identification hash and then to the uid (pinned by a test; the key logic is unchanged).
    /// </summary>
    public static List<AccountRow> ToRows(IReadOnlyList<ConnectedAccount> accounts)
    {
        var suggested = accounts.ToList().FindIndex(a => string.Equals(a.CashAccountType?.Trim(), CurrentAccountType, StringComparison.OrdinalIgnoreCase));
        return [.. accounts.Select((a, i) => new AccountRow(
            (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayName(a),
            Kind(a.CashAccountType),
            Last4(a.Iban),
            i == suggested))];
    }

    private static string DisplayName(ConnectedAccount account) => AccountName(account) ?? "Account";

    /// <summary>The account's name, else its product (control characters stripped, at most 100 characters); null when it has neither.</summary>
    public static string? AccountName(ConnectedAccount account)
    {
        foreach (var candidate in new[] { account.Name, account.Product })
        {
            var clean = new string([.. (candidate ?? "").Where(c => !char.IsControl(c))]).Trim();
            if (clean.Length > 0)
            {
                return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
            }
        }

        return null;
    }

    private static string Kind(string? cashAccountType) => cashAccountType?.Trim().ToUpperInvariant() switch
    {
        "CACC" => "current",
        "SVGS" => "savings",
        "CARD" => "card",
        "LOAN" => "loan",
        _ => "account",
    };

    /// <summary>The last four characters of the IBAN, upper case; null for no IBAN or one shorter than 8 characters (never a whole short value).</summary>
    public static string? Last4(string? iban)
    {
        var compact = new string([.. (iban ?? "").Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();
        return compact.Length >= 8 ? compact[^4..] : null;
    }

    // --- POST /connect-attempts/{id}/choice ----------------------------------------------------

    public static async Task<IResult> ChooseAsync(
        string? id, ChoiceRequest? request, ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher,
        ConsentStore consent, IFirstSyncTrigger firstSync, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(Category);
        try
        {
            var now = clock.GetUtcNow();
            var (row, refusal) = await LoadLiveAsync(id, user, db, eb, cipher, now, logger, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var choice = WaitingPayload.Open(cipher, row!.Id, row.WaitingPayload!);
            var rowIds = ToRows(choice.Accounts).Select(r => r.Id).ToList();
            var index = request?.AccountId is { } wanted ? rowIds.IndexOf(wanted) : -1;
            if (index < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountId"] = ["Not one of the offered accounts."] });
            }

            // The claim: one write, guarded by Version, that already says "saved" and drops the payload. A second choice
            // that read the same row loses here and is refused; the session id lives on in this call only. If the
            // store below fails, the attempt is marked failed and the session closed.
            row.Outcome = ConnectOutcome.Saved;
            row.FailureReason = null;
            row.WaitingPayload = null;
            row.Version = unchecked(row.Version + 1);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return NotWaiting();
            }

            // From here on the request token is not used any more: the attempt already reads "saved" and the session id
            // lives only in this call, so a client that hangs up must not leave "saved" without a consent.
            var userId = row.UserId;
            var aspsp = row.AspspName;
            // StoreConsentAsync never throws and closes the new session itself (once) when it fails.
            var stored = await ConsentEndpoints.StoreConsentAsync(
                userId, aspsp, row.Country, choice.SessionId, choice.Accounts[index], choice.ValidUntil, now, consent, eb, logger);

            if (!stored.Stored)
            {
                await ConsentEndpoints.MarkAsync(db, row, ConnectOutcome.Failed, ConnectFailure.StoreFailed, logger);
                return Results.Problem(
                    "The bank could not be saved. Start again.", statusCode: StatusCodes.Status502BadGateway, title: "Could not save the bank");
            }

            try
            {
                await ConsentEndpoints.AfterStoredAsync(stored.OldSessionId, choice.SessionId, aspsp, eb, firstSync, logger);
            }
            catch (Exception ex)
            {
                // The consent is stored; the first sync and the old session are best effort and never undo it.
                logger.LogWarning("The steps after saving failed ({ExceptionType}); the consent stays.", ex.GetType().Name);
            }

            return Results.Ok(new { state = "saved" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(logger, ex);
        }
    }

    // --- POST /connect-attempts/{id}/cancel ----------------------------------------------------

    public static async Task<IResult> CancelAsync(
        string? id, ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(Category);
        try
        {
            var (row, refusal) = await LoadLiveAsync(id, user, db, eb, cipher, clock.GetUtcNow(), logger, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var attemptId = row!.Id;
            var aspsp = row.AspspName;
            var payload = row.WaitingPayload!;

            // The claim first (guarded by Version; the payload stays): from here the attempt reads failed/cancelled and
            // a parallel choice is refused. If the close below fails, the payload is still there and the cleanup of
            // Story 5.3 retries, because a row that is no longer a live choice but holds a payload is due.
            row.Outcome = ConnectOutcome.Failed;
            row.FailureReason = ConnectFailure.Cancelled;
            row.Version = unchecked(row.Version + 1);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return NotWaiting();
            }

            var closed = false;
            try
            {
                var choice = WaitingPayload.Open(cipher, attemptId, payload);
                closed = await ConnectAttempts.CloseSessionAsync(eb, choice.SessionId, aspsp, logger, "The waiting session");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreadable (wrong key): there is no handle to close with, so the payload goes.
                logger.LogWarning("A waiting choice of {Aspsp} could not be read ({ExceptionType}); its payload is deleted.", aspsp, ex.GetType().Name);
                closed = true;
            }

            if (closed)
            {
                await ClearPayloadAsync(db, attemptId, logger);
            }

            return Results.Ok(new { state = "failed", reason = ConnectFailure.Cancelled });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(logger, ex);
        }
    }

    /// <summary>Deletes the payload of a closed session, best effort: the cleanup of Story 5.3 would do it later anyway.</summary>
    private static async Task ClearPayloadAsync(GreedyNoseDbContext db, Guid attemptId, ILogger logger)
    {
        try
        {
            db.ChangeTracker.Clear();
            var row = await db.PendingConsents.SingleOrDefaultAsync(p => p.Id == attemptId);
            if (row is { WaitingPayload: not null })
            {
                row.WaitingPayload = null;
                row.Version = unchecked(row.Version + 1);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not delete the payload of a cancelled choice ({ExceptionType}); the cleanup will.", ex.GetType().Name);
            db.ChangeTracker.Clear();
        }
    }

    // --- shared ------------------------------------------------------------------------------

    /// <summary>
    /// The attempt of this user if it is a live choice (outcome choose-account, payload present, deadline not passed),
    /// else the refusal: 404 for a malformed, unknown or foreign id, 409 for any other state. The cleanup of Story 5.3
    /// runs first, so a choice whose deadline passed is closed and refused here.
    /// </summary>
    private static async Task<(PendingConsent? Row, IResult? Refusal)> LoadLiveAsync(
        string? id, ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher,
        DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var attemptId))
        {
            return (null, Results.NotFound());
        }

        await ConnectAttempts.CloseExpiredChoicesAsync(db, eb, cipher, now, logger, ct);

        var row = await db.PendingConsents.SingleOrDefaultAsync(p => p.Id == attemptId && p.UserId == user.UserId, ct);
        if (row is null)
        {
            return (null, Results.NotFound());
        }

        var live = row.Outcome == ConnectOutcome.ChooseAccount && row.WaitingPayload is not null && row.ChoiceDeadline > now;
        return live ? (row, null) : (null, NotWaiting());
    }

    private static IResult NotWaiting() => Results.Problem(
        "This attempt is not waiting for a choice.", statusCode: StatusCodes.Status409Conflict, title: "Not waiting for a choice");

    private static IResult Unavailable(ILogger logger, Exception ex)
    {
        // Type only: a message can carry connection details.
        logger.LogError("The account choice failed ({ExceptionType}).", ex.GetType().Name);
        return Results.Problem(title: "The account choice is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
