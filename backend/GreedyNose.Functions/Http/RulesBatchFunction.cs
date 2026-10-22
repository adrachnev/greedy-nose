using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Rules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

public sealed class RulesBatchFunction(GreedyNoseDbContext db, ICurrentUser user, TimeProvider clock, ILoggerFactory loggers)
{
    [Function("RulesBatch")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/batch")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await JsonBody.ReadAsync<RulesBatchRequest>(req, JsonBody.DefaultMaxBytes, ct);
        if (body.Error is not null)
        {
            return body.Error.ToActionResult();
        }

        return (await RulesBatchEndpoint.HandleAsync(body.Value, db, user, clock, loggers, ct)).ToActionResult();
    }
}
