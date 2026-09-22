using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// Fetches and maps one connected account's transactions. The seam between "talk to Enable
/// Banking" and the ingestion logic — <see cref="IngestionRunner"/> depends on this, not on
/// <see cref="EnableBankingClient"/> directly, so its tests run <see cref="TransactionMapper.Map"/>
/// for real against a fixture while never opening a socket (mirrors <c>INotificationSender</c>'s
/// role for the send side).
/// </summary>
public interface IDebitsFetcher
{
    Task<MappedDebits> FetchAsync(ConnectedAccount account, CancellationToken ct);
}
