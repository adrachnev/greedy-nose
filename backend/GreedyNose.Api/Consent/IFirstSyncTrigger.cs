namespace GreedyNose.Api.Consent;

/// <summary>
/// Asks for a sync right after a consent was stored (Story 2.4), so the owner does not wait for the
/// next scheduled tick. A request, not a guarantee: the caller treats a failure as non-fatal because
/// the scheduled tick runs the same sync anyway. Implementations carry no data about the consent —
/// the sync reads everything it needs from the database.
/// </summary>
public interface IFirstSyncTrigger
{
    Task RequestAsync(CancellationToken ct);
}
