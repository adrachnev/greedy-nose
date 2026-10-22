using GreedyNose.Api.Auth;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Banks;

/// <summary>
/// <c>GET /banks?q=</c> (Story 5.2, R36): searches Enable Banking's bank list by part of a name. Contract:
/// the text is trimmed and must be 2..100 characters (else 400); the match is a case- and accent-insensitive
/// "contains" on the name; the answer is at most <see cref="MaxHits"/> banks sorted by name, then country, each with name,
/// country and whether the user already has a consent for it (any state, name compared ignoring case). An empty
/// list is a normal answer; a list that cannot be read is 502. The answer holds nothing else, and the search
/// text is never logged. Behind the API key like every endpoint (the route is not exempt).
/// </summary>
public static class BanksEndpoint
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int MaxHits = 20;

    public static async Task<IResult> HandleAsync(
        string? q, ICurrentUser user, ConsentStore consent, BankCatalogue catalogue, TimeProvider clock,
        ILogger<BankCatalogue> logger, CancellationToken ct)
    {
        var text = (q ?? "").Trim();
        if (text.Length < MinQueryLength || text.Length > MaxQueryLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = [$"Type {MinQueryLength} to {MaxQueryLength} characters of the bank's name."],
            });
        }

        var banks = await catalogue.GetAsync(ct);
        if (banks is null)
        {
            return Results.Problem(
                "The bank list could not be loaded. Try again later.",
                statusCode: StatusCodes.Status502BadGateway, title: "Bank list unavailable");
        }

        // Matching ignores case and accents; sorting stays on the real name.
        var needle = SearchFold.Fold(text);
        var hits = banks
            .Where(b => b.Folded.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Country, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHits)
            .ToList();

        HashSet<string> connected;
        try
        {
            // Restored once per host, not per search (loading decrypts every consent). A consent stored by
            // another instance after that shows up on the next restore; a stale mark is harmless here.
            var now = clock.GetUtcNow();
            await consent.EnsureRestoredAsync(now, ct);
            // LIMIT: compared by name only. BankConsent stores no country, so one name in several countries
            // counts as the same bank until the country is stored.
            connected = new HashSet<string>(
                consent.GetStates(now).Where(s => s.UserId == user.UserId && s.AspspName is not null).Select(s => s.AspspName!),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not read the connections for the bank search ({ExceptionType}).", ex.GetType().Name);
            return Results.Problem(title: "The bank search is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            banks = hits.Select(b => new { name = b.Name, country = b.Country, connected = connected.Contains(b.Name) }).ToList(),
        });
    }
}
