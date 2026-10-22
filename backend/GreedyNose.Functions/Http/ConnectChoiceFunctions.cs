using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

/// <summary>The account choice of an attempt (Story 5.4): the list, the choice and the cancel. API key like every route but the two browser pages.</summary>
public sealed class ConnectChoiceFunctions(
    ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher, ConsentStore consent,
    IFirstSyncTrigger firstSync, TimeProvider clock, ILoggerFactory loggers)
{
    [Function("ConnectAttemptAccounts")]
    public async Task<IActionResult> Accounts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connect-attempts/{id}/accounts")] HttpRequest req,
        string id,
        CancellationToken ct) =>
        (await ConnectChoice.AccountsAsync(id, user, db, eb, cipher, clock, loggers, ct)).ToActionResult();

    [Function("ConnectAttemptChoice")]
    public async Task<IActionResult> Choose(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connect-attempts/{id}/choice")] HttpRequest req,
        string id,
        CancellationToken ct)
    {
        var body = await JsonBody.ReadAsync<ChoiceRequest>(req, 10_000, ct);
        if (body.Error is not null)
        {
            return body.Error.ToActionResult();
        }

        return (await ConnectChoice.ChooseAsync(id, body.Value, user, db, eb, cipher, consent, firstSync, clock, loggers, ct)).ToActionResult();
    }

    [Function("ConnectAttemptCancel")]
    public async Task<IActionResult> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connect-attempts/{id}/cancel")] HttpRequest req,
        string id,
        CancellationToken ct) =>
        (await ConnectChoice.CancelAsync(id, user, db, eb, cipher, clock, loggers, ct)).ToActionResult();
}
