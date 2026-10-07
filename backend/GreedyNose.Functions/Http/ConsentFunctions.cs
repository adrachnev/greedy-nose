using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

/// <summary>
/// The consent round trip (Story 1.4): <c>POST /connect-links</c> behind the API key, and the two
/// browser pages <c>GET /connect</c> and <c>GET /callback</c>, which authenticate by their own one-time
/// secrets. Scoped dependencies arrive per invocation (one DI scope per Function call).
/// </summary>
public sealed class ConsentFunctions(
    GreedyNoseDbContext db,
    EnableBankingClient eb,
    EnableBankingOptions options,
    PendingConsentOptions pending,
    ConsentStore consent,
    IFirstSyncTrigger firstSync,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    [Function("CreateConnectLink")]
    public async Task<IActionResult> CreateLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connect-links")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await JsonBody.ReadAsync<ConnectLinkRequest>(req, JsonBody.DefaultMaxBytes, ct);
        if (body.Error is not null)
        {
            return body.Error.ToActionResult();
        }

        return (await ConsentEndpoints.CreateLinkAsync(body.Value, db, options, pending, clock, loggers, ct)).ToActionResult();
    }

    [Function("Connect")]
    public async Task<IActionResult> Connect(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connect")] HttpRequest req,
        CancellationToken ct) =>
        (await ConsentEndpoints.ConnectAsync(req.Query.Text("token"), db, eb, options, clock, loggers, ct)).ToActionResult();

    [Function("Callback")]
    public async Task<IActionResult> Callback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "callback")] HttpRequest req,
        CancellationToken ct) =>
        (await ConsentEndpoints.CallbackAsync(
            req.Query.Text("code"), req.Query.Text("state"), req.Query.Text("error"),
            db, eb, consent, firstSync, clock, loggers, ct)).ToActionResult();
}

internal static class QueryExtensions
{
    /// <summary>A query value as the minimal-API binder gave it: absent is null, repeated values are joined.</summary>
    public static string? Text(this IQueryCollection query, string name) =>
        query.TryGetValue(name, out var value) ? value.ToString() : null;
}
