using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Status;

namespace GreedyNose.Api.Health;

/// <summary>
/// <c>GET /health</c>: is the backend up, and what is the worst real state of the user's banks. Behind the API
/// key like everything else. Details per bank live in <c>GET /status</c>; this answer carries no account,
/// application or URL data. Like <c>/status</c> it restores the consents from the database first (a cold host
/// would otherwise say "none"). The database read has a short budget: if it fails or is too slow the backend is
/// still up, so the answer is <c>status: ok</c> with <c>state: unknown</c> (exception type logged, no message).
/// </summary>
public static class HealthEndpoint
{
    internal static readonly TimeSpan DatabaseBudget = TimeSpan.FromSeconds(3);

    public static async Task<IResult> HandleAsync(
        ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, TimeProvider clock,
        ILogger<ConsentStore> logger, CancellationToken ct)
    {
        string state;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(DatabaseBudget);
            var now = clock.GetUtcNow();
            await consent.RestoreAsync(now, budget.Token);
            var banks = await ConnectionStatusReader.ReadAsync(consent, db, user.UserId, now, budget.Token);
            state = ConnectionStates.Worst(banks.Select(b => b.State));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The health check could not read the connection state ({ExceptionType}).", ex.GetType().Name);
            state = "unknown";
        }

        return Results.Ok(new { status = "ok", state });
    }
}
