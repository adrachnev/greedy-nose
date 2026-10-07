using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace GreedyNose.Functions.Http;

/// <summary>
/// Catches every path no other Function owns, so an unknown path still passes through the API-key
/// middleware and answers 401 without a key (the check must not reveal which routes exist). With a
/// valid key it answers 404. Literal routes outrank a catch-all in route precedence, so it only gets
/// what nothing else matched.
/// </summary>
public sealed class UnknownRouteFunction
{
    [Function("UnknownRoute")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete", "patch", "head", "options", Route = "{*path}")]
        HttpRequest req) =>
        new NotFoundResult();
}
