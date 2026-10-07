using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Health;

/// <summary>
/// <c>GET /health</c>: what the app and the owner look at to see whether the backend is up and which
/// consent it holds. Behind the API key like everything else. Reads memory only (no database), so a
/// database that is down does not stop it from answering.
/// </summary>
public static class HealthEndpoint
{
    public static IResult Handle(ConsentStore consent, EnableBankingOptions options, TimeProvider clock) =>
        Results.Ok(new
        {
            status = "ok",
            applicationId = options.ApplicationId,
            baseUrl = options.BaseUrl,
            connected = consent.SessionId is not null,
            // active / expired / none, judged now (R19) — `connected` only says a session id is remembered.
            state = consent.GetState(clock.GetUtcNow()).Status.ToString().ToLowerInvariant(),
            aspsp = consent.AspspName,
            account = consent.PrimaryAccount,
            connectedAt = consent.ConnectedAt,
            expiresAt = consent.ExpiresAt,
        });
}
