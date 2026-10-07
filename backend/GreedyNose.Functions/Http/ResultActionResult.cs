using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GreedyNose.Functions.Http;

/// <summary>
/// Lets a Function return what the endpoint handlers already return. The handlers answer with
/// <see cref="IResult"/> (minimal-API style); the Functions ASP.NET Core integration wants an
/// <see cref="IActionResult"/>. This runs the <see cref="IResult"/> against the request's own
/// <see cref="HttpContext"/>, so status, headers, body and wrappers such as <c>BrowserResult</c>
/// behave exactly as they did under the minimal-API host.
/// </summary>
public sealed class ResultActionResult(IResult result) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
}

public static class ResultExtensions
{
    public static IActionResult ToActionResult(this IResult result) => new ResultActionResult(result);
}
