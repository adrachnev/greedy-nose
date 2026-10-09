using System.Text.Json;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// A non-2xx answer from Enable Banking, typed so <see cref="IngestionRunner"/> can tell a bank-side
/// abort from our own failure (R19). Derives from <see cref="InvalidOperationException"/> because that
/// is what the fetcher threw before, and callers/tests that catch that keep working.
///
/// Carries only the status, the <c>error</c> code (<c>CLOSED_SESSION</c>, …) and Enable Banking's own
/// <c>message</c> text (422 and 429 only, cut to 200 characters), never the rest of the body: the body can
/// echo account data, and neither the message nor a log line may hold it. A 401 without an
/// <c>error</c> field ("Wrong signature", i.e. our own key) has a null <see cref="ErrorCode"/>.
/// </summary>
public sealed class EnableBankingRequestException(int statusCode, string? errorCode, string? apiMessage = null)
    : InvalidOperationException(
        $"Enable Banking answered {statusCode}{(errorCode is null ? "" : $" ({errorCode})")}{(apiMessage is null ? "" : $": {apiMessage}")} fetching transactions for the connected account.")
{
    public int StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;

    public static EnableBankingRequestException FromResponse(EnableBankingResponse response) =>
        new(
            response.StatusCode,
            ReadStringField(response.Body, "error"),
            // Only the generic validation (422) and rate-limit (429) statuses: their wording is Enable
            // Banking's own. Other statuses (401, 5xx, gateway pages) may carry other text, so code only.
            response.StatusCode is 422 or 429 ? CleanMessage(ReadStringField(response.Body, "message")) : null);

    private const int MaxMessageLength = 200;

    // Enable Banking's own error wording (e.g. "Wrong signature"), not bank data; still cut and
    // stripped of control characters so a odd body cannot flood or forge a log line.
    private static string? CleanMessage(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var cleaned = new string(message.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxMessageLength)
        {
            cleaned = cleaned[..MaxMessageLength];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    // An unreadable or differently shaped body is "no value", never a reason to fail the failure path.
    private static string? ReadStringField(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var error)
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
