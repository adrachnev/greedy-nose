using System.Text.Json;

namespace GreedyNose.Api.EnableBanking;

public enum AspspLookup
{
    /// <summary>Exactly one bank in Enable Banking's list matches the name and country.</summary>
    Resolved,

    /// <summary>The list was read and no bank matches.</summary>
    NotFound,

    /// <summary>The list was read and more than one bank matches. Never guessed.</summary>
    Ambiguous,

    /// <summary>The list could not be read (HTTP error, unreadable answer, network fault, timeout).</summary>
    ListUnavailable,
}

/// <summary>
/// The outcome of a bank lookup. <see cref="MaximumConsentValiditySeconds"/> is the bank's own
/// <c>maximum_consent_validity</c>, null when the list does not state one.
/// </summary>
public sealed record AspspResolution(
    AspspLookup Outcome,
    string Name = "",
    string Country = "",
    long? MaximumConsentValiditySeconds = null);

/// <summary>
/// Finds a bank in Enable Banking's own list (<c>GET /aspsps?country=XX</c>) by exact name and country
/// (ordinal, ignoring case) — the only place a bank name becomes a bank, so nothing else in the backend
/// needs to know one (R22). The list is read fresh on every call, no cache: it is two small requests per
/// connect and a stale limit would be worse than the cost. The answer body is never logged or returned
/// (the Sandbox list carries test credentials).
/// </summary>
public sealed class AspspResolver(EnableBankingClient eb)
{
    public async Task<AspspResolution> ResolveAsync(string name, string country, CancellationToken ct)
    {
        EnableBankingResponse response;
        try
        {
            response = await eb.GetAsync($"/aspsps?country={Uri.EscapeDataString(country)}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new AspspResolution(AspspLookup.ListUnavailable);
        }

        if (!response.IsSuccess)
        {
            return new AspspResolution(AspspLookup.ListUnavailable);
        }

        List<AspspResolution> matches = [];
        try
        {
            var root = JsonDocument.Parse(response.Body).RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("aspsps", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return new AspspResolution(AspspLookup.ListUnavailable);
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var itemName = Text(item, "name");
                var itemCountry = Text(item, "country");
                if (itemName is null || itemCountry is null
                    || !string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(itemCountry, country, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(new AspspResolution(AspspLookup.Resolved, itemName, itemCountry, Seconds(item)));
            }
        }
        catch (JsonException)
        {
            return new AspspResolution(AspspLookup.ListUnavailable);
        }

        return matches.Count switch
        {
            0 => new AspspResolution(AspspLookup.NotFound),
            1 => matches[0],
            _ => new AspspResolution(AspspLookup.Ambiguous),
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Seconds(JsonElement element)
    {
        if (!element.TryGetProperty("maximum_consent_validity", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var whole))
        {
            return whole;
        }

        return value.TryGetDouble(out var fraction) && double.IsFinite(fraction) && fraction < long.MaxValue
            ? (long)fraction
            : null;
    }
}

/// <summary>How long a consent is requested for: the configured lifetime, cut to the bank's own maximum.</summary>
public static class ConsentValidity
{
    /// <summary>
    /// The smaller of <paramref name="configured"/> and the bank's <c>maximum_consent_validity</c> (seconds).
    /// A missing or non-positive maximum counts as "not stated" and leaves the configured lifetime.
    /// </summary>
    public static TimeSpan For(TimeSpan configured, long? maximumSeconds) =>
        maximumSeconds is > 0 && maximumSeconds < (long)configured.TotalSeconds
            ? TimeSpan.FromSeconds(maximumSeconds.Value)
            : configured;
}
