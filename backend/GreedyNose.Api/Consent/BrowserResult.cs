using Microsoft.AspNetCore.Http;

namespace GreedyNose.Api.Consent;

/// <summary>
/// Wraps the result of a browser page (<c>/connect</c>, <c>/callback</c>) to add headers it needs:
/// the URL carries a one-time secret, so nothing may be cached and no Referer may leak it onward.
/// <see cref="Inner"/> is the plain result, for tests.
/// </summary>
public sealed class BrowserResult(IResult inner) : IResult
{
    public IResult Inner => inner;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        return inner.ExecuteAsync(httpContext);
    }
}
