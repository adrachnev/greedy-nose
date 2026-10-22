using GreedyNose.Api.Consent;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions;

/// <summary>
/// Reloads the consent from the database before every tick, so a bank connected or reconnected is seen
/// without restarting this host (and by another instance of it). The database is the source of truth;
/// nothing is carried over from the previous run.
/// A failing restore (wrong key, database down or waking up) skips this tick and is logged at Error —
/// exception type only, never its message, which can carry connection details. The host keeps running.
/// </summary>
public sealed class ConsentRestoringTick(
    ConsentStore consent,
    IIngestionTick inner,
    TimeProvider clock,
    ILogger<ConsentRestoringTick> logger,
    ConnectAttemptCleanup attempts) : IIngestionTick
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await RestoreAndRunAsync(ct);
        }
        finally
        {
            // Story 5.3: after the ingestion work, whatever it came to: an account choice that ran out gets its
            // waiting session closed. Its own short budget, because the tick's token may be spent by a slow bank;
            // it swallows every fault itself, and running out of budget must not replace the tick's own result.
            using var budget = new CancellationTokenSource(CleanupBudget);
            try
            {
                await attempts.RunAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Closing expired account choices ran out of time; it will be tried again at the next tick.");
            }
        }
    }

    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);

    private async Task RestoreAndRunAsync(CancellationToken ct)
    {
        try
        {
            await consent.RestoreAsync(clock.GetUtcNow(), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Could not restore the consent before the tick ({ExceptionType}); skipping this tick.",
                ex.GetType().Name);
            return;
        }

        await inner.RunOnceAsync(ct);
    }
}
