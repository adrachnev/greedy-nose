using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Status;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

public sealed class StatusFunction(
    ICurrentUser user, ConsentStore consent, GreedyNoseDbContext db, TimeProvider clock, ILogger<ConsentStore> logger)
{
    [Function("Status")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req,
        CancellationToken ct) =>
        (await StatusEndpoint.HandleAsync(user, consent, db, clock, logger, ct)).ToActionResult();
}
