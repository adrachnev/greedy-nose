using System.Text.Json;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Ingestion;

/// <summary>
/// The production <see cref="ISessionStatusChecker"/>: <c>GET /sessions/{id}</c>, read <c>status</c>.
/// A closed session stays readable (verified 2026-09-29, status <c>CLOSED</c>), which is what makes
/// this a usable confirmation. Every failure — transport, non-2xx, unreadable body — is "unknown".
/// Neither the session id nor the body is ever logged.
/// </summary>
public sealed class EnableBankingSessionStatusChecker(
    IHttpClientFactory httpClientFactory,
    EnableBankingSigner signer,
    TimeProvider clock,
    ILogger<EnableBankingSessionStatusChecker> logger) : ISessionStatusChecker
{
    public async Task<string?> GetStatusAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var client = EnableBankingClient.Create(httpClientFactory, signer, clock);
            var result = await client.GetAsync($"/sessions/{Uri.EscapeDataString(sessionId)}", ct);

            if (!result.IsSuccess)
            {
                logger.LogWarning("Enable Banking answered {StatusCode} checking the session status.", result.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(result.Body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(status.GetString())
                ? status.GetString()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException
                                          && !ct.IsCancellationRequested)
        {
            // TaskCanceledException here is the HttpClient's own timeout; a caller cancellation
            // (ct) still propagates.
            logger.LogWarning("Could not establish the session status ({ExceptionType}).", exception.GetType().Name);
            return null;
        }
    }
}
