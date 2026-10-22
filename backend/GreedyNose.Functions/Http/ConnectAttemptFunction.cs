using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

/// <summary><c>GET /connect-attempts/{id}</c> (Story 5.3): how far a consent started from the app has come. API key like every route but the two browser pages.</summary>
public sealed class ConnectAttemptFunction(
    ICurrentUser user, GreedyNoseDbContext db, EnableBankingClient eb, ConsentCipher cipher, TimeProvider clock, ILoggerFactory loggers)
{
    [Function("ConnectAttempt")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connect-attempts/{id}")] HttpRequest req,
        string id,
        CancellationToken ct) =>
        (await ConnectAttempts.HandleAsync(id, user, db, eb, cipher, clock, loggers, ct)).ToActionResult();
}
