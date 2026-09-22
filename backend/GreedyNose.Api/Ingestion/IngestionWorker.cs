namespace GreedyNose.Api.Ingestion;

/// <summary>
/// Runs <see cref="IngestionRunner.RunOnceAsync"/> on a fixed cadence
/// (<c>Ingestion:PollIntervalSeconds</c>). A singleton for the process lifetime — see
/// <see cref="EnableBankingDebitsFetcher"/> for why it must never hold a captive
/// <c>EnableBankingClient</c>, and <c>Program.cs</c> for why it takes
/// <c>IDbContextFactory&lt;GreedyNoseDbContext&gt;</c> only indirectly, through
/// <see cref="IngestionRunner"/>.
/// </summary>
public sealed class IngestionWorker(
    IngestionRunner runner,
    IngestionOptions options,
    TimeProvider clock,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    /// <summary>
    /// Generous against step 3's documented worst case (~100s: FirebaseAdmin's internal retries
    /// plus a black-holed-connection timeout) — a per-tick deadline so one stuck tick cannot block
    /// every future poll forever.
    /// </summary>
    public static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(120);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, clock);

        do
        {
            await RunTickAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One tick, isolated: any non-cancellation failure is logged at Error and swallowed here, so a
    /// bad tick cannot silently kill all future polling (decided 2026-09-22). A caught
    /// <see cref="OperationCanceledException"/> is either this tick's own timeout (logged, move on)
    /// or the host shutting down (<paramref name="stoppingToken"/> itself fired) — told apart by
    /// which token is cancelled, not by catching two different exception types.
    /// </summary>
    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(TickTimeout);

        try
        {
            await runner.RunOnceAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown, not a tick timeout — let ExecuteAsync's loop condition end quietly.
        }
        catch (OperationCanceledException)
        {
            logger.LogError("An ingestion tick timed out after {Timeout}; will try again next interval.", TickTimeout);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An ingestion tick failed; will try again next interval.");
        }
    }
}
