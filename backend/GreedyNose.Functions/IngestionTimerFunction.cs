using GreedyNose.Api.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions;

/// <summary>
/// Runs exactly one <see cref="IIngestionTick"/> per timer trigger (Story 2.3, AR5). Keeps no state
/// between runs: every tick re-reads everything it needs from the database.
///
/// Schedule: app setting <c>IngestionSchedule</c> (NCRONTAB, UTC), floor enforced in Program.cs.
/// <c>RunOnStartup = false</c>: a cold start or scale-out must not cost a bank fetch.
/// <c>UseMonitor = false</c>: with the monitor on, the host persists schedule occurrences and fires a
/// past-due run after a restart — an extra bank fetch we cannot afford (N26 ~4/day). With it off, a
/// run missed while the app was down is simply skipped; the next scheduled one runs. Singleton across
/// instances still holds (blob lease on AzureWebJobsStorage, per the timer-trigger docs).
/// </summary>
public sealed class IngestionTimerFunction(IIngestionTick tick, ILogger<IngestionTimerFunction> logger)
{
    /// <summary>
    /// Generous against the documented worst case (~100 s: FirebaseAdmin's internal retries plus a
    /// black-holed-connection timeout) — a per-tick deadline so one stuck tick cannot hold the
    /// singleton lock forever.
    /// </summary>
    public static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(120);

    [Function("IngestionTick")]
    public Task Run(
        // The name is flat ON PURPOSE: the Functions host resolves %name% through configuration, which turns
        // "__" into ":" in env var names, so "%Ingestion__Schedule%" does not resolve (seen live, Story 2.3).
        // Do not rename it to a sectioned name.
        [TimerTrigger("%IngestionSchedule%", RunOnStartup = false, UseMonitor = false)] TimerInfo timer,
        FunctionContext context) =>
        RunTickAsync(context.CancellationToken, TickTimeout);

    /// <summary>
    /// One tick, isolated: any non-cancellation failure is logged at Error and swallowed, so a bad
    /// tick cannot stop the next trigger. A caught <see cref="OperationCanceledException"/> is either
    /// this tick's own timeout (logged) or the host shutting down (<paramref name="hostToken"/> itself
    /// fired) — told apart by which token is cancelled. Internal so tests can pass a short timeout.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken hostToken, TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await tick.RunOnceAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            logger.LogWarning("An ingestion tick was cancelled because the host is shutting down.");
        }
        catch (OperationCanceledException)
        {
            logger.LogError("An ingestion tick timed out after {Timeout}; will try again at the next trigger.", timeout);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An ingestion tick failed; will try again at the next trigger.");
        }
    }
}
