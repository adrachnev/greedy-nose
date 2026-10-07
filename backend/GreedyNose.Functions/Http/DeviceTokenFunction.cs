using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions.Http;

public sealed class DeviceTokenFunction(GreedyNoseDbContext db, ICurrentUser user, TimeProvider clock, ILoggerFactory loggers)
{
    [Function("DeviceToken")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "device-token")] HttpRequest req,
        CancellationToken ct)
    {
        // The handler's [RequestSizeLimit] is a Kestrel feature a Function does not get: same limit, enforced here.
        var body = await JsonBody.ReadAsync<DeviceTokenRequest>(req, DeviceTokenEndpoint.MaxBodyBytes, ct);
        if (body.Error is not null)
        {
            return body.Error.ToActionResult();
        }

        return (await DeviceTokenEndpoint.HandleAsync(body.Value, db, user, clock, loggers, ct)).ToActionResult();
    }
}
