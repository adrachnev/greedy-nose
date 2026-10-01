using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Auth;

/// <summary>
/// Requires <c>Authorization: Bearer &lt;key&gt;</c> on every request (Story 1.3). Protected by
/// default, so a future endpoint cannot be forgotten; unknown paths also answer 401, so the check
/// does not reveal which routes exist. The key is never logged — a rejection logs only the path.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private const string Scheme = "Bearer";

    // The only paths reachable without the key: browser pages that cannot send a header.
    // Removed in Story 1.4.
    private static readonly PathString[] Exempt = [new("/connect"), new("/callback")];

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedHash;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, ApiKeyOptions options, ILogger<ApiKeyMiddleware> logger)
    {
        options.Validate();
        _next = next;
        _logger = logger;
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Key));
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (IsExempt(context.Request.Path) || HasValidKey(context.Request.Headers.Authorization.ToString()))
        {
            return _next(context);
        }

        // Path only — never the header. The path is not secret; the key is.
        // The caller controls the path, so it is sanitized and truncated before it reaches the log.
        _logger.LogWarning("Rejected a request without a valid API key: {Path}", SanitizePath(context.Request.Path.Value));
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = Scheme;
        return Task.CompletedTask;
    }

    internal const int MaxLoggedPathLength = 100;

    internal static string SanitizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var truncated = path.Length > MaxLoggedPathLength;
        var chars = (truncated ? path[..MaxLoggedPathLength] : path)
            .Select(c => char.IsControl(c) ? '?' : c)
            .ToArray();
        return new string(chars) + (truncated ? "..." : "");
    }

    // StartsWithSegments so /connectx is not exempt; the remainder must be empty (or a lone slash),
    // so /connect/../health-style paths are not exempt either, whatever the server normalizes.
    private static bool IsExempt(PathString path)
    {
        foreach (var exempt in Exempt)
        {
            if (path.StartsWithSegments(exempt, out var rest) && (!rest.HasValue || rest.Value == "/"))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasValidKey(string header)
    {
        // Exactly "Bearer <token>": one space, non-empty token. Extra spaces fail by design.
        var prefix = Scheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[prefix.Length..];
        if (token.Length == 0 || token.Contains(' '))
        {
            return false;
        }

        // Hash both sides so the comparison is fixed-time and hides the key's length.
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, _expectedHash);
    }
}
