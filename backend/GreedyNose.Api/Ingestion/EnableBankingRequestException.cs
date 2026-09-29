using System.Text.Json;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// A non-2xx answer from Enable Banking, typed so <see cref="IngestionRunner"/> can tell a bank-side
/// abort from our own failure (R19). Derives from <see cref="InvalidOperationException"/> because that
/// is what the fetcher threw before, and callers/tests that catch that keep working.
///
/// Carries only the status and the <c>error</c> code (<c>CLOSED_SESSION</c>, …), never the body: the
/// body can echo account data, and neither the message nor a log line may hold it. A 401 without an
/// <c>error</c> field ("Wrong signature", i.e. our own key) has a null <see cref="ErrorCode"/>.
/// </summary>
public sealed class EnableBankingRequestException(int statusCode, string? errorCode)
    : InvalidOperationException(
        $"Enable Banking answered {statusCode}{(errorCode is null ? "" : $" ({errorCode})")} fetching transactions for the connected account.")
{
    public int StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;

    public static EnableBankingRequestException FromResponse(EnableBankingResponse response) =>
        new(response.StatusCode, ReadErrorCode(response.Body));

    // An unreadable or differently shaped body is "no code", never a reason to fail the failure path.
    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(error.GetString())
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
