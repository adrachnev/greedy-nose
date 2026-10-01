namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Configuration for talking to Enable Banking. The application ID and the key path come from
/// user secrets (see TRACER-01-BANK-DATA.md, "Secrets") — the private key must never reach git.
/// </summary>
public sealed class EnableBankingOptions
{
    public const string SectionName = "EnableBanking";

    /// <summary>Base address of the API. The old api.tilisy.com host is deprecated.</summary>
    public string BaseUrl { get; set; } = "https://api.enablebanking.com";

    /// <summary>
    /// The registered application's ID. It is also the filename of the downloaded key, which is
    /// how you can always recover it: &lt;application-id&gt;.pem.
    /// </summary>
    public string ApplicationId { get; set; } = "";

    /// <summary>Absolute path to the RSA private key (PKCS#8 PEM) issued at registration.</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// How long a generated JWT stays valid. Enable Banking caps this at 24h; short is fine
    /// because we mint one per request from a key that is already in memory.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Must match the URL registered in the Enable Banking control panel character for character,
    /// or POST /auth rejects it.
    /// </summary>
    public string RedirectUrl { get; set; } = "http://localhost:5199/callback";

    /// <summary>
    /// Which bank /connect uses when the query string doesn't say. A default, never an assumption:
    /// R22 forbids hardcoding a bank anywhere it matters.
    /// </summary>
    public string DefaultAspsp { get; set; } = "Mock ASPSP";

    public string DefaultCountry { get; set; } = "DE";

    /// <summary>
    /// Requested consent lifetime. PSD2 commonly grants ~90 days; ASPSPs advertise their own cap
    /// as maximum_consent_validity in GET /aspsps.
    /// </summary>
    public TimeSpan ConsentValidity { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Where GET /raw drops what the bank sent. Kept out of git: it is real account data, and
    /// after step 6 it is the owner's own. See TRACER-01-BANK-DATA.md step 3.
    /// </summary>
    public string RawDumpDirectory { get; set; } = "raw";

    /// <summary>
    /// Where the live consent is remembered across restarts, relative to the content root. Holds
    /// a session id that reads a real bank account, so it is gitignored like the key and the raw
    /// dumps — it is a credential, not a cache.
    /// </summary>
    public string ConsentFilePath { get; set; } = "consent.local.json";
}
