using System.Text;
using System.Text.Json;

namespace GreedyNose.Api.EnableBanking;

/// <summary>What Enable Banking answered, passed through untouched.</summary>
public sealed record EnableBankingResponse(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Thin HTTP client over Enable Banking. It deliberately returns the raw response body instead of
/// deserialized models: step 3 of the tracer bullet wants to see exactly what the bank sends, and
/// an error body from them is far more useful than a swallowed exception.
/// </summary>
public sealed class EnableBankingClient(HttpClient http, EnableBankingSigner signer, TimeProvider clock)
{
    /// <summary>Their API is snake_case throughout; ours is not, so the policy does the mapping.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<EnableBankingResponse> GetAsync(string pathAndQuery, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, pathAndQuery), ct);

    public Task<EnableBankingResponse> PostAsync(string path, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };

        return SendAsync(request, ct);
    }

    private async Task<EnableBankingResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", signer.CreateToken(clock.GetUtcNow()));

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            return new EnableBankingResponse((int)response.StatusCode, body);
        }
    }
}
