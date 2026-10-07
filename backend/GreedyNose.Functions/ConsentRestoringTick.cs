using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions;

/// <summary>
/// Reloads the consent from the database before every tick, so a bank connected or reconnected through
/// the API process is seen without restarting this host (until Story 2.4 they are separate processes).
/// The database is the source of truth; nothing is carried over from the previous run.
/// A failing restore (wrong key, database down or waking up) skips this tick and is logged at Error —
/// exception type only, never its message, which can carry connection details. The host keeps running.
/// </summary>
public sealed class ConsentRestoringTick(
    ConsentStore consent,
    IIngestionTick inner,
    TimeProvider clock,
    ILogger<ConsentRestoringTick> logger) : IIngestionTick
{
    public async Task RunOnceAsync(CancellationToken ct)
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
