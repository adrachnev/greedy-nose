using GreedyNose.Api.Auth;
using GreedyNose.Api.Banks;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

/// <summary>My banks with their state, and the repeat of a failed first sync (Story 5.5). API key like every route but the two browser pages.</summary>
public sealed class MyBanksFunctions(
    ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, IFirstSyncTrigger firstSync, TimeProvider clock,
    ILogger<MyBanksFunctions> logger)
{
    [Function("MyBanks")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "my-banks")] HttpRequest req,
        CancellationToken ct) =>
        (await MyBanksEndpoint.ListAsync(user, consent, db, clock, logger, ct)).ToActionResult();

    [Function("MyBanksFirstSync")]
    public async Task<IActionResult> FirstSync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "my-banks/first-sync")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await JsonBody.ReadAsync<FirstSyncRequest>(req, 10_000, ct);
        if (body.Error is not null)
        {
            return body.Error.ToActionResult();
        }

        return (await MyBanksEndpoint.FirstSyncAsync(body.Value, user, consent, db, firstSync, clock, logger, ct)).ToActionResult();
    }
}
