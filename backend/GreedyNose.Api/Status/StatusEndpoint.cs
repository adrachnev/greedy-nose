using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Status;

/// <summary>
/// <c>GET /status</c> (Story 4.2, AR15): one entry per connected bank of the current user — state, valid
/// until, last success, last attempt, short last error — and how many debit alerts still wait for their push
/// (open <c>Debit</c> marks; a reconnect summary is not a debit). Postcondition: the answer holds the bank's
/// public name, states, dates and counts only; no session id, token, account number, IBAN, uid, payee name,
/// amount, application id or URL. The consent store is restored from the database first, because a callback
/// or a tick on another instance may have changed it. Any database failure answers 503 without details.
/// </summary>
public static class StatusEndpoint
{
    public static async Task<IResult> HandleAsync(
        ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, TimeProvider clock,
        ILogger<ConsentStore> logger, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            await consent.RestoreAsync(now, ct);

            var userId = user.UserId;
            var banks = await ConnectionStatusReader.ReadAsync(consent, db, userId, now, ct);
            var waiting = await db.PendingPushes.AsNoTracking()
                .CountAsync(p => p.UserId == userId && p.Kind == PendingPushKind.Debit && p.EndedAt == null, ct);

            return Results.Ok(new
            {
                banks = banks.Select(b => new
                {
                    aspsp = b.Aspsp,
                    state = b.State,
                    validUntil = b.ValidUntil,
                    lastSuccessAt = b.LastSuccessAt,
                    lastAttemptAt = b.LastAttemptAt,
                    lastError = b.LastError,
                }).ToList(),
                debitsWaitingForPush = waiting,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the message can carry connection details.
            logger.LogError("Could not read the connection status ({ExceptionType}).", ex.GetType().Name);
            return Results.Problem(title: "The connection status is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
