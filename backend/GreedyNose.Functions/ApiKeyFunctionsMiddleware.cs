using GreedyNose.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Functions;

/// <summary>
/// The API-key check for the HTTP Functions (Story 2.4): the Functions host does not run the ASP.NET
/// Core pipeline, so the rule (<see cref="ApiKeyGate"/>, shared and unchanged) runs as Functions
/// middleware. Registered after <c>ConfigureFunctionsWebApplication</c>, before any Function, and only
/// for HTTP-triggered invocations; the timer and queue Functions have no caller to authenticate.
/// A rejected request never reaches a Function: the 401 is written on the request's own HttpContext.
/// </summary>
public sealed class ApiKeyFunctionsMiddleware(ApiKeyGate gate, ILogger<ApiKeyFunctionsMiddleware> logger)
    : IFunctionsWorkerMiddleware
{
    /// <summary>
    /// Decided by the trigger type, not by "is an HttpContext available": if the HttpContext were
    /// missing for an HTTP trigger, the check must fail closed (see <see cref="Invoke"/>), not be skipped.
    /// </summary>
    public static bool IsHttpInvocation(FunctionContext context) =>
        context.FunctionDefinition.InputBindings.Values.Any(b =>
            string.Equals(b.Type, "httpTrigger", StringComparison.OrdinalIgnoreCase));

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext()
                   ?? throw new InvalidOperationException("An HTTP-triggered invocation has no HttpContext; refusing to skip the API-key check.");

        await InvokeAsync(http, () => next(context));
    }

    /// <summary>The check itself, separate from Functions types so tests drive it with a plain HttpContext.</summary>
    public async Task InvokeAsync(HttpContext http, Func<Task> next)
    {
        if (gate.IsAllowed(http))
        {
            await next();
            return;
        }

        ApiKeyGate.Reject(http, logger);
    }
}
