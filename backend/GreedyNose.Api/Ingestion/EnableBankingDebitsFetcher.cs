using System.Globalization;
using System.Text.Json;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// The one production implementation of <see cref="IDebitsFetcher"/>, and the fix for the
/// captive-dependency bug flagged in TRACER-02-NOTIFICATIONS.md's step 6 plan: a singleton
/// <see cref="IngestionWorker"/> cannot hold a transient typed <c>HttpClient</c>
/// (<see cref="EnableBankingClient"/>) for the process lifetime — the handler would never rotate.
/// Instead this builds a fresh <see cref="EnableBankingClient"/> every call from
/// <see cref="IHttpClientFactory"/>, reusing <c>Program.cs</c>'s existing BaseAddress/Timeout
/// configuration for the <c>nameof(EnableBankingClient)</c>-named client
/// (<c>AddHttpClient&lt;EnableBankingClient&gt;</c> registers under the type's short name).
/// </summary>
public sealed class EnableBankingDebitsFetcher(
    IHttpClientFactory httpClientFactory,
    EnableBankingSigner signer,
    TimeProvider clock,
    EnableBankingOptions options,
    ILogger<EnableBankingDebitsFetcher> logger) : IDebitsFetcher
{
    /// <summary>Safety cap for the paging loop; 12 months is a handful of pages.</summary>
    private const int MaxPages = 200;

    private bool _baseAddressChecked;

    public async Task<MappedDebits> FetchAsync(ConnectedAccount account, DateOnly dateFrom, CancellationToken ct)
    {
        var client = EnableBankingClient.Create(httpClientFactory, signer, clock);

        // Once per process, not once per tick: cheap enough to always run, and it is exactly the
        // sanity check the plan asked for — a silently wrong client name would otherwise send
        // every request to a bare, un-configured HttpClient (no BaseAddress) and fail oddly.
        if (!_baseAddressChecked)
        {
            _baseAddressChecked = true;
            var expected = new Uri(options.BaseUrl);
            if (client.BaseAddress != expected)
            {
                logger.LogError(
                    "The named HttpClient {Name} has BaseAddress {Actual}, expected {Expected} — " +
                    "the ingestion worker would be calling the wrong host or none at all.",
                    nameof(EnableBankingClient), client.BaseAddress, expected);
            }
        }

        var path = $"/accounts/{account.Uid}/transactions";

        // Every tick: from dateFrom to the end, every page (the caller decides the date).
        var dateFromText = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var transactions = new List<EbTransaction>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        string? continuationKey = null;
        var pages = 0;

        do
        {
            if (pages >= MaxPages)
            {
                throw new InvalidOperationException(
                    $"Enable Banking still offered a next page after {MaxPages} pages; giving up.");
            }

            var query = $"{path}?date_from={dateFromText}";
            if (continuationKey is not null)
            {
                query += $"&continuation_key={Uri.EscapeDataString(continuationKey)}";
            }

            var page = await GetPageAsync(client, query, ct);
            pages++;
            transactions.AddRange(page.Transactions ?? []);

            continuationKey = string.IsNullOrEmpty(page.ContinuationKey) ? null : page.ContinuationKey;
            if (continuationKey is not null && !usedKeys.Add(continuationKey))
            {
                throw new InvalidOperationException("Enable Banking repeated a continuation key; giving up.");
            }
        }
        while (continuationKey is not null);

        logger.LogInformation("Tick fetched {Pages} page(s) of transactions from {DateFrom}.", pages, dateFromText);

        // One Map call over everything, so the fallback ids' ordinals count across page boundaries.
        return TransactionMapper.Map(new EbTransactionsResponse(transactions, null), account.Key);
    }

    private static async Task<EbTransactionsResponse> GetPageAsync(EnableBankingClient client, string pathAndQuery, CancellationToken ct)
    {
        var result = await client.GetAsync(pathAndQuery, ct);

        if (!result.IsSuccess)
        {
            // Typed, so the runner can tell a bank abort (401 + error code) from our own failures.
            throw EnableBankingRequestException.FromResponse(result);
        }

        return JsonSerializer.Deserialize<EbTransactionsResponse>(result.Body, EnableBankingClient.Json)
            ?? throw new InvalidOperationException("Enable Banking returned a body we could not read.");
    }
}
