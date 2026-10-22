using System.Text.Json;
using GreedyNose.Api.Banks;

namespace GreedyNose.Api.EnableBanking;

/// <summary>One bank of Enable Banking's list as the search shows it: name and country, nothing else.</summary>
/// <param name="Folded">The name folded once for the search (<see cref="SearchFold"/>); never part of an answer.</param>
public sealed record BankEntry(string Name, string Country, string Folded);

/// <summary>
/// Story 5.2: Enable Banking's bank list for the search only, kept in memory for <see cref="CacheLifetime"/>.
/// Only name and country are kept — the answer body (the Sandbox list carries test credentials) is dropped
/// right after parsing and never logged. One refresh at a time; a failed read is not cached, so the next
/// search tries again. Starting a consent does not use this: <see cref="AspspResolver"/> still reads the list
/// fresh (a stale consent limit would be worse than the cost). No bank is named here (R22).
/// </summary>
public sealed class BankCatalogue(Func<EnableBankingClient> clients, TimeProvider clock, ILogger<BankCatalogue> logger)
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _refreshGate = new();
    private Task<IReadOnlyList<BankEntry>?>? _inflight; // guarded by _refreshGate
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyList<BankEntry> Banks, DateTimeOffset FetchedAt);

    /// <summary>The whole list, or null when it cannot be read (and nothing fresh is cached).</summary>
    public async Task<IReadOnlyList<BankEntry>?> GetAsync(CancellationToken ct)
    {
        if (Fresh() is { } cached)
        {
            return cached.Banks;
        }

        Task<IReadOnlyList<BankEntry>?> refresh;
        lock (_refreshGate)
        {
            // Everyone who arrives while a read is running waits for that one read and shares its outcome
            // (success or failure), instead of retrying one after another while Enable Banking is down.
            _inflight ??= Task.Run(RefreshAsync, CancellationToken.None);
            refresh = _inflight;
        }

        // The read itself does not take the caller's token: one caller giving up must not fail the others or
        // leave the refresh dangling. The client's own timeout bounds it.
        return await refresh.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<BankEntry>?> RefreshAsync()
    {
        try
        {
            var banks = await ReadAsync(CancellationToken.None);
            if (banks is not null)
            {
                _snapshot = new Snapshot(banks, clock.GetUtcNow());
            }

            return banks;
        }
        finally
        {
            // Failures are not cached: the next search after this one starts a new read.
            lock (_refreshGate)
            {
                _inflight = null;
            }
        }
    }

    private Snapshot? Fresh()
    {
        var snapshot = _snapshot;
        return snapshot is not null && clock.GetUtcNow() - snapshot.FetchedAt < CacheLifetime ? snapshot : null;
    }

    private async Task<IReadOnlyList<BankEntry>?> ReadAsync(CancellationToken ct)
    {
        EnableBankingResponse response;
        try
        {
            response = await clients().GetAsync("/aspsps", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Type only: a message can carry the URL.
            logger.LogWarning("The bank list could not be read ({ExceptionType}).", ex.GetType().Name);
            return null;
        }

        if (!response.IsSuccess)
        {
            logger.LogWarning("The bank list could not be read (HTTP {Status}).", response.StatusCode);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("aspsps", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("The bank list had an unexpected shape.");
                return null;
            }

            List<BankEntry> banks = [];
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && Text(item, "name") is { Length: > 0 } name
                    && Text(item, "country") is { Length: > 0 } country)
                {
                    banks.Add(new BankEntry(name, country, SearchFold.Fold(name)));
                }
            }

            return banks;
        }
        catch (JsonException)
        {
            logger.LogWarning("The bank list was not readable JSON.");
            return null;
        }
    }

    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape (\ud800) cannot be read as a string: this one bank is skipped, not the list.
            return null;
        }
    }
}
