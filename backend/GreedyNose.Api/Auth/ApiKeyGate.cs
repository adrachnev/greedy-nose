using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Auth;

/// <summary>
/// The API-key rule itself (Story 1.3), host-independent so every host runs the same check:
/// <c>Authorization: Bearer &lt;key&gt;</c> on every request except the two browser pages. Protected by
/// default, so a future endpoint cannot be forgotten; unknown paths also answer 401, so the check
/// does not reveal which routes exist. The key is never logged — a rejection logs only the path.
/// </summary>
public sealed class ApiKeyGate
{
    private const string Scheme = "Bearer";

    // The only paths reachable without the key: browser pages that cannot send a header. The
    // exemption stays (Story 1.4): each page authenticates by its own one-time secret instead —
    // /connect by the link token, /callback by the OAuth state (see ConsentEndpoints). The link is
    // created by POST /connect-links, a different path segment, which this gate protects.
    private static readonly PathString[] Exempt = [new("/connect"), new("/callback")];

    private readonly byte[] _expectedHash;

    public ApiKeyGate(ApiKeyOptions options)
    {
        options.Validate();
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Key));
    }

    public bool IsAllowed(HttpContext context) =>
        IsExempt(context.Request.Path) || HasValidKey(context.Request.Headers.Authorization.ToString());

    /// <summary>Answers 401 and logs the (sanitized) path — never the header.</summary>
    public static void Reject(HttpContext context, ILogger logger)
    {
        // The caller controls the path, so it is sanitized and truncated before it reaches the log.
        logger.LogWarning("Rejected a request without a valid API key: {Path}", SanitizePath(context.Request.Path.Value));
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = Scheme;
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
