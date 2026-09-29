namespace GreedyNose.Api.Ingestion;

/// <summary>
/// Asks the bank whether a consent session is still <c>AUTHORIZED</c> — the confirmation step of R19's
/// bank-abort signal. A seam like <see cref="IDebitsFetcher"/>, so <see cref="IngestionRunner"/>'s
/// tests never open a socket.
/// </summary>
public interface ISessionStatusChecker
{
    /// <summary>
    /// The session's <c>status</c> (<c>AUTHORIZED</c>, <c>CLOSED</c>, …), or null when it could not be
    /// established — a failed call and an unreadable answer both mean "unknown", never "dead".
    /// </summary>
    Task<string?> GetStatusAsync(string sessionId, CancellationToken ct);
}
