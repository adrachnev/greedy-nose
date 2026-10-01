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
    private bool _baseAddressChecked;

    public async Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct)
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

        var result = await client.GetAsync($"/accounts/{account.Uid}/transactions", ct);

        if (!result.IsSuccess)
        {
            // Typed, so the runner can tell a bank abort (401 + error code) from our own failures.
            throw EnableBankingRequestException.FromResponse(result);
        }

        var response = JsonSerializer.Deserialize<EbTransactionsResponse>(result.Body, EnableBankingClient.Json)
            ?? throw new InvalidOperationException("Enable Banking returned a body we could not read.");

        return TransactionMapper.Map(response, account.Key);
    }
}
