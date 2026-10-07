using GreedyNose.Api.Ingestion;

namespace GreedyNose.Functions;

/// <summary>
/// Lets at most one tick run at a time in this process (Story 2.4, bead gn-w4l). The timer Function and
/// the first-sync queue Function can fire together. Two overlapping ticks of the same account would each
/// find the same new debit, each send its push before either writes the NotificationLog row (the unique
/// index only catches the second INSERT, after the push has gone out), and each send a reconnect
/// summary. Waiting here costs nothing: the second tick starts when the first ends, sees the committed
/// state and finds nothing new. Waiting honours the caller's token, so a timeout still applies.
///
/// Scope: one process. Several Function app instances at once (consumption scale-out) are not covered;
/// see the bead for the cross-instance case.
/// </summary>
public sealed class SerializedTick(IIngestionTick inner) : IIngestionTick
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await inner.RunOnceAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
