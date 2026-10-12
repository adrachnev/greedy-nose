using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Status;

/// <summary>The states a bank's connection can be in (Story 4.2). The strings are the wire values.</summary>
public static class ConnectionStates
{
    public const string Connected = "connected";
    public const string Expired = "expired";
    public const string Dead = "dead";
    public const string FirstSyncPending = "first sync pending";
    public const string None = "none";

    // Worst first; used for the one-word summary of /health.
    private static readonly string[] WorstFirst = [Dead, Expired, FirstSyncPending, Connected];

    /// <summary>The worst of the given states, <see cref="None"/> when the list is empty.</summary>
    public static string Worst(IEnumerable<string> states)
    {
        var set = states.ToHashSet();
        return WorstFirst.FirstOrDefault(set.Contains) ?? None;
    }
}

/// <summary>
/// One bank as the status page shows it. Contract: only the bank's public name, a state, dates and a short
/// error code — never a session id, account data or anything else that identifies an account.
/// </summary>
public sealed record BankStatus(
    string? Aspsp,
    string State,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError);

/// <summary>
/// Works out each bank's real state for one user from the consent store (memory) plus the database rows the
/// worker keeps. Order of judgement: past <c>valid_until</c> is <c>expired</c>; else the bank ended it and
/// the expiry push was announced for this very session (Stories 4.1/4.3) is <c>dead</c>; else no
/// first-sync row is <c>first sync pending</c>; else <c>connected</c>. A dead consent not yet announced still
/// reads as before until the next tick records it — "dead" is never inferred from <c>LastError</c>, which the
/// first attempt after a reconnect overwrites.
/// </summary>
public static class ConnectionStatusReader
{
    public static async Task<IReadOnlyList<BankStatus>> ReadAsync(
        ConsentStore consent, GreedyNoseDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var states = consent.GetStates(now).Where(s => s.UserId == userId).ToList();
        if (states.Count == 0)
        {
            return [];
        }

        var rows = await db.BankConsents.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct);
        var synced = (await db.AccountSyncStates.AsNoTracking().Where(a => a.UserId == userId)
            .Select(a => a.AccountKey).ToListAsync(ct)).ToHashSet();

        var result = new List<BankStatus>();
        foreach (var s in states)
        {
            var row = rows.FirstOrDefault(r => r.AspspName == (s.AspspName ?? ""));
            string state;
            if (s.Status == ConsentStatus.Expired)
            {
                state = ConnectionStates.Expired;
            }
            else if (row?.ExpiryNotifiedSessionHash is { } told && told == SessionFingerprint.Of(s.SessionId!))
            {
                state = ConnectionStates.Dead;
            }
            else if (!synced.Contains(s.Account!.Key))
            {
                state = ConnectionStates.FirstSyncPending;
            }
            else
            {
                state = ConnectionStates.Connected;
            }

            result.Add(new BankStatus(s.AspspName, state, s.ValidUntil, row?.LastSuccessAt, row?.LastAttemptAt, row?.LastError));
        }

        return result;
    }
}
