namespace GreedyNose.Api.Alerts;

/// <summary>
/// Operator-mail configuration (Story 2.10). <c>Brevo:ApiKey</c> is a secret (user secrets locally, Key
/// Vault later), the rest is a plain setting. All four are required at startup: a push channel that fails
/// without anyone being told is exactly what this mail exists for, so a host that could not send it
/// should not start. The messages name the setting, never a value.
/// </summary>
public sealed class BrevoOptions
{
    public const string SectionName = "Brevo";

    public string ApiKey { get; set; } = "";

    public string SenderEmail { get; set; } = "";

    public string SenderName { get; set; } = "";
}

/// <summary>Where the operator mail goes (<c>Alerts:RecipientEmail</c>).</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    public string RecipientEmail { get; set; } = "";
}

public static class AlertOptionsValidation
{
    /// <summary>Throws <see cref="InvalidOperationException"/> naming every missing setting; never prints a value.</summary>
    public static void Validate(BrevoOptions brevo, AlertOptions alerts)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(brevo.ApiKey)) missing.Add("Brevo:ApiKey");
        if (string.IsNullOrWhiteSpace(brevo.SenderEmail)) missing.Add("Brevo:SenderEmail");
        if (string.IsNullOrWhiteSpace(brevo.SenderName)) missing.Add("Brevo:SenderName");
        if (string.IsNullOrWhiteSpace(alerts.RecipientEmail)) missing.Add("Alerts:RecipientEmail");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The alert mail is not configured: {string.Join(", ", missing)} not set. Set them with " +
                "`dotnet user-secrets set \"<name>\" \"<value>\"` (the API key is a secret; never commit it).");
        }
    }
}
