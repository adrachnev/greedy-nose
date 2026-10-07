using GreedyNose.Api.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions;

/// <summary>
/// The sync right after a consent (Story 2.4): runs the same restoring tick as the timer, once per
/// queue message (queue <see cref="QueueFirstSyncTrigger.QueueName"/>, put there by <c>/callback</c>).
/// It reads the consent from the database, so the message carries nothing.
///
/// Unlike the timer Function it does NOT swallow a failure: the exception goes back to the host, the
/// message becomes visible again after <c>visibilityTimeout</c> and is retried up to
/// <c>maxDequeueCount</c> times, then moves to <c>first-sync-poison</c> (all in host.json). The first
/// sync is all-or-nothing, so a retry starts clean. A message that ends in poison is not lost work: the
/// next scheduled tick runs the same sync. A consent that cannot be restored is the one failure the
/// shared tick logs and skips instead of throwing (see <see cref="ConsentRestoringTick"/>); that case
/// is picked up by the next scheduled tick, not by a queue retry.
/// </summary>
public sealed class FirstSyncFunction(IIngestionTick tick, ILogger<FirstSyncFunction> logger)
{
    /// <summary>
    /// A 12-month first sync pages through the bank's history, so it gets far more than the timer's
    /// 120 s, and less than the host's <c>functionTimeout</c> (10 min, the consumption-plan maximum).
    /// </summary>
    // Visibility, as seen in the live host log: the queue trigger hides a dequeued message for 10 min
    // (visibilitytimeout=600) while it runs. That is longer than this 8 min timeout, so a message cannot
    // reappear mid-sync. host.json's "visibilityTimeout" is a different thing: only the delay before a
    // message that FAILED becomes visible again for its retry.
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(8);

    [Function("FirstSync")]
    public Task Run(
        [QueueTrigger(QueueFirstSyncTrigger.QueueName)] string message,
        FunctionContext context) =>
        RunSyncAsync(context.CancellationToken, SyncTimeout);

    /// <summary>Internal so tests can pass a short timeout.</summary>
    internal async Task RunSyncAsync(CancellationToken hostToken, TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await tick.RunOnceAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            logger.LogWarning("The first sync was cancelled because the host is shutting down; the message will be retried.");
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("The first sync timed out after {Timeout}; the message will be retried.", timeout);
            throw;
        }
        catch (Exception ex)
        {
            // Type only: a message can carry connection details. The host also logs the rethrown exception.
            logger.LogError("The first sync failed ({ExceptionType}); the message will be retried.", ex.GetType().Name);
            throw;
        }
    }
}
