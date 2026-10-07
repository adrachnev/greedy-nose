using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Debits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace GreedyNose.Functions.Http;

public sealed class DebitsFunction(ICurrentUser user, GreedyNoseDbContext db)
{
    [Function("Debits")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "debits")] HttpRequest req,
        CancellationToken ct) =>
        (await DebitsEndpoint.HandleAsync(user, db, ct)).ToActionResult();
}
