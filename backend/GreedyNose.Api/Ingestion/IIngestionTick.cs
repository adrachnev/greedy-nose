namespace GreedyNose.Api.Ingestion;

/// <summary>
/// One ingestion tick. The seam between the scheduler (the timer Function, Story 2.3) and
/// <see cref="IngestionRunner"/>, so the scheduler's policy can be tested without a database.
/// Contract: the scheduler calls it once per trigger and owns timeout and failure handling.
/// </summary>
public interface IIngestionTick
{
    Task RunOnceAsync(CancellationToken ct);
}
