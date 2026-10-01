namespace GreedyNose.Api.Consent;

/// <summary>Settings for the browser consent flow (Story 1.4). No credential in here.</summary>
public sealed class PendingConsentOptions
{
    public const string SectionName = "PendingConsent";

    /// <summary>
    /// How long a started consent stays valid, counted from the link's creation. Long enough to
    /// approve in the bank's own app, short enough that a forgotten link is useless.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(30);
}
