using System.Text;

namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// The only per-request trace of an Enable Banking call (gn-6yh). Microsoft.Extensions.Http's default
/// logging is removed in <c>AddGreedyNoseCore</c> because it prints the full URL, and the path carries a
/// session id or an account uid. This line keeps what the ops checks need — which call, which status, so
/// fetches against the daily quota and a 429 stay countable — and nothing else: every path segment that is
/// not a known fixed word is replaced by <c>{id}</c>, and there is no query, header, body or exception message (the exception type only).
/// </summary>
public sealed class RedactedRequestLogHandler(ILogger<RedactedRequestLogHandler> logger) : DelegatingHandler
{
    /// <summary>
    /// Fail closed: only these fixed segments of their API are printed as they are; every other segment
    /// (session id, account uid, anything unknown, encoded or short) becomes <c>{id}</c>. A new endpoint
    /// with a new fixed word shows as <c>{id}</c> until it is added here, which is the safe direction.
    /// </summary>
    private static readonly HashSet<string> KnownSegments = new(StringComparer.Ordinal)
    {
        "auth", "sessions", "accounts", "transactions", "balances", "aspsps", "application",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var template = PathTemplate(request.RequestUri);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // The type only: the message of an HttpRequestException can carry the URL.
            logger.LogWarning("Enable Banking {Method} {PathTemplate} failed: {ExceptionType}",
                method, template, ex.GetType().Name);
            throw;
        }

        logger.LogInformation("Enable Banking {Method} {PathTemplate} -> {StatusCode}",
            method, template, (int)response.StatusCode);
        return response;
    }

    /// <summary>The path of the request with every id-like segment replaced by <c>{id}</c>; no query.</summary>
    public static string PathTemplate(Uri? uri)
    {
        if (uri is null)
        {
            return "/";
        }

        // A relative URI (before the client applied its BaseAddress) has no AbsolutePath.
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', '#')[0];

        var result = new StringBuilder();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Append('/').Append(LooksLikeId(segment) ? "{id}" : segment);
        }

        return result.Length == 0 ? "/" : result.ToString();
    }

    private static bool LooksLikeId(string segment) => !KnownSegments.Contains(segment);
}
