using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace GreedyNose.Functions.Http;

// The HTTP Functions (Story 2.4) are deliberately thin: bind, call the handler, adapt the result. The
// handlers keep their minimal-API shape and are what the tests pin. AuthorizationLevel.Anonymous on
// purpose: the Functions host key is not our auth, ApiKeyFunctionsMiddleware is. Routes carry no
// "api/" prefix (host.json routePrefix is empty), so the app and tools/connect-bank.sh keep their URLs.
public sealed class HealthFunction(ConsentStore consent, EnableBankingOptions options, TimeProvider clock)
{
    [Function("Health")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req) =>
        HealthEndpoint.Handle(consent, options, clock).ToActionResult();
}
