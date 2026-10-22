using GreedyNose.Api.Auth;
using GreedyNose.Api.Banks;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

public sealed class BanksFunction(
    ICurrentUser user, ConsentStore consent, BankCatalogue catalogue, TimeProvider clock, ILogger<BankCatalogue> logger)
{
    [Function("Banks")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "banks")] HttpRequest req,
        CancellationToken ct) =>
        (await BanksEndpoint.HandleAsync(req.Query.Text("q"), user, consent, catalogue, clock, logger, ct)).ToActionResult();
}
