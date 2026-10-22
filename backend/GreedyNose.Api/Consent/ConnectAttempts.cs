using System.Collections.Concurrent;
using System.Text.Json;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Consent;

/// <summary>What waits encrypted on an attempt while the owner chooses an account (Story 5.3; read in Story 5.4).</summary>
public sealed record WaitingChoice(string SessionId, IReadOnlyList<ConnectedAccount> Accounts);

/// <summary>
/// Seals the session id and the euro accounts into <see cref="PendingConsent.WaitingPayload"/>. Same cipher as the
/// stored consents; the associated data binds the value to its row id and column, so a payload copied to another
/// row (or into another column) fails to open. Nothing here logs.
/// </summary>
public static class WaitingPayload
{
    private const string Column = "WaitingPayload";
    private static readonly JsonSerializerOptions Json = new();

    private static string Aad(Guid attemptId) => $"{attemptId:D}|{Column}";

    public static byte[] Seal(ConsentCipher cipher, Guid attemptId, WaitingChoice choice) =>
        cipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(choice, Json), Aad(attemptId));

    /// <exception cref="ConsentDecryptionException">Wrong key, tampered value, wrong row or unreadable content.</exception>
    public static WaitingChoice Open(ConsentCipher cipher, Guid attemptId, byte[] sealedValue)
    {
        var bytes = cipher.Decrypt(sealedValue, Aad(attemptId));
        try
        {
            return JsonSerializer.Deserialize<WaitingChoice>(bytes, Json)
                   ?? throw new ConsentDecryptionException("A waiting choice is not readable.");
        }
        catch (JsonException exception)
        {
            throw new ConsentDecryptionException("A waiting choice is not readable.", exception);
        }
    }
}

/// <summary>
/// The attempt the app follows (Story 5.3): <c>GET /connect-attempts/{id}</c> and the cleanup of choices that ran
/// out. Contract of the read: needs the API key (checked before the handler); a malformed, unknown or foreign id is
/// 404; the answer is <c>{state, reason?}</c> with state one of waiting, choose_account, saved, failed, expired and a
/// reason only for failed (a short code). It never holds a session id, IBAN, account name or account list, and no
/// log line holds them either.
/// </summary>
public static class ConnectAttempts
{
    private static readonly string Category = typeof(ConnectAttempts).FullName!;
    private static readonly TimeSpan CloseSessionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the owner has to choose an account, counted from the callback.</summary>
    public static readonly TimeSpan ChoiceLifetime = TimeSpan.FromMinutes(15);

    public static async Task<IResult> HandleAsync(
        string? id, ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var attemptId))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var logger = loggers.CreateLogger(Category);

        // A choice that ran out is closed before it is reported. "Expired" means the time is over; the session is
        // normally gone too, but while the bank refuses the close it is retried (with a pause) and stays open.
        await CloseExpiredChoicesAsync(db, eb, cipher, now, logger, ct);

        var row = await db.PendingConsents.AsNoTracking().SingleOrDefaultAsync(p => p.Id == attemptId && p.UserId == user.UserId, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        return row.Outcome switch
        {
            ConnectOutcome.Saved => Results.Ok(new { state = "saved" }),
            ConnectOutcome.Failed => Results.Ok(new { state = "failed", reason = row.FailureReason }),
            ConnectOutcome.ChooseAccount => Results.Ok(new { state = row.ChoiceDeadline > now ? "choose_account" : "expired" }),
            _ => Results.Ok(new { state = row.ExpiresAt > now ? "waiting" : "expired" }),
        };
    }

    /// <summary>How long after its deadline a choice is still retried when the bank will not close its session.</summary>
    public static readonly TimeSpan CloseRetryWindow = TimeSpan.FromDays(1);

    /// <summary>The pause between two tries to close the same waiting session, per instance (the tick comes every 6 h, so it always passes).</summary>
    public static readonly TimeSpan CloseBackoff = TimeSpan.FromMinutes(5);

    private const int MaxRememberedTries = 200;

    // Row id -> when this instance last failed to close its session. In memory on purpose (no schema): a restart only
    // means one early retry. Entries go when the payload is gone; the size is bounded in case a row never finishes.
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastFailedClose = new();

    /// <summary>
    /// Closes the waiting session of every row that holds a payload outside its live window — a choice whose deadline
    /// has passed, or a row of any other outcome that still has one (a failed or saved row whose payload was left
    /// behind) — and deletes the payload once the session is closed. The row stays (a choice answers "expired") until
    /// its own deadline. A close the bank did not accept keeps the payload, because the session id in it is the only
    /// handle to the open session; a state read, a new link or the tick tries again, but not within
    /// <see cref="CloseBackoff"/> of this instance's last failed try, until <see cref="CloseRetryWindow"/> after the
    /// deadline. Then it tries once more, gives up, deletes the payload and logs one warning (bank name only). The
    /// token also interrupts a close. Never throws except for the caller's cancellation.
    /// </summary>
    public static async Task CloseExpiredChoicesAsync(
        GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        try
        {
            var due = await db.PendingConsents
                .Where(p => p.WaitingPayload != null && (p.Outcome != ConnectOutcome.ChooseAccount || p.ChoiceDeadline <= now))
                .ToListAsync(ct);

            var finished = new List<Guid>();
            foreach (var row in due)
            {
                ct.ThrowIfCancellationRequested();
                var giveUpAt = (row.ChoiceDeadline ?? row.CompletedAt ?? row.CreatedAt) + CloseRetryWindow;
                var lastResort = now >= giveUpAt;
                if (!lastResort && LastFailedClose.TryGetValue(row.Id, out var last) && now - last < CloseBackoff)
                {
                    continue;
                }

                var closed = false;
                try
                {
                    var choice = WaitingPayload.Open(cipher, row.Id, row.WaitingPayload!);
                    closed = await CloseSessionAsync(eb, choice.SessionId, row.AspspName, logger, "The waiting session", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Cannot be read (wrong key): there is no handle to close with, so the payload goes.
                    logger.LogWarning("A waiting choice of {Aspsp} could not be read ({ExceptionType}); its payload is deleted.", row.AspspName, ex.GetType().Name);
                    closed = true;
                }

                if (!closed && !lastResort)
                {
                    if (LastFailedClose.Count >= MaxRememberedTries)
                    {
                        LastFailedClose.Clear();
                    }

                    LastFailedClose[row.Id] = now;
                    continue; // keep the payload; a later read or tick retries
                }

                if (!closed)
                {
                    logger.LogWarning("The waiting session of {Aspsp} could not be closed for {Window}; giving up and deleting its payload.", row.AspspName, CloseRetryWindow);
                }

                row.WaitingPayload = null;
                row.Version = unchecked(row.Version + 1);
                finished.Add(row.Id);
            }

            if (finished.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                foreach (var id in finished)
                {
                    LastFailedClose.TryRemove(id, out _);
                }
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another instance cleaned the same rows first; the goal is met.
            db.ChangeTracker.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: a message can carry connection details. The next read or tick tries again.
            logger.LogWarning("Closing expired choices failed ({ExceptionType}).", ex.GetType().Name);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Closes a session at Enable Banking, best effort: nothing here can fail the caller. Returns true when the session
    /// is closed or was already gone ("closed", "already closed / expired / unknown", as in Story 4.3), false when
    /// the bank did not accept it, could not be reached, took longer than 5 s or the caller's token was cancelled.
    /// Logs carry the bank name and the status only, never the session id.
    /// </summary>
    public static async Task<bool> CloseSessionAsync(
        EnableBankingClient eb, string sessionId, string aspspName, ILogger logger, string what, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CloseSessionTimeout);
            timeout.Token.ThrowIfCancellationRequested(); // do not even start a close the caller has given up on
            var answer = await eb.DeleteAsync($"/sessions/{Uri.EscapeDataString(sessionId)}", timeout.Token);
            if (answer.IsSuccess || answer.StatusCode is 404 or 410 || (answer.StatusCode == 401 && HasErrorField(answer.Body)))
            {
                logger.LogInformation("{What} of {Aspsp} was closed or already gone (HTTP {Status}).", what, aspspName, answer.StatusCode);
                return true;
            }

            logger.LogWarning("{What} of {Aspsp} could not be closed (HTTP {Status}).", what, aspspName, answer.StatusCode);
        }
        catch (Exception ex)
        {
            // Type only: a message can carry the URL, which holds the session id.
            logger.LogWarning("{What} of {Aspsp} could not be closed ({ExceptionType}).", what, aspspName, ex.GetType().Name);
        }

        return false;
    }

    private static bool HasErrorField(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// The ingestion tick's small call (Story 5.3): closes the choices that ran out, with its own database context and
/// client (the tick is not a request). Never throws except for the caller's cancellation.
/// </summary>
public sealed class ConnectAttemptCleanup(
    IDbContextFactory<GreedyNoseDbContext> dbFactory, Func<EnableBankingClient> clientFactory, ConsentCipher cipher,
    TimeProvider clock, ILogger<ConnectAttemptCleanup> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await ConnectAttempts.CloseExpiredChoicesAsync(db, clientFactory(), cipher, clock.GetUtcNow(), logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Closing expired choices failed ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
