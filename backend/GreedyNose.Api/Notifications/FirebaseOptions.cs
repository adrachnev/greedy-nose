namespace GreedyNose.Api.Notifications;

/// <summary>
/// Configuration for sending pushes through Firebase Cloud Messaging. The key path comes from user
/// secrets, never appsettings.json: the service-account JSON is the whole credential for sending to
/// every install of the app, exactly like the Enable Banking private key (TRACER-02-NOTIFICATIONS.md,
/// step 0). It stays server-side and never goes near <c>app/</c>.
/// </summary>
public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    /// <summary>
    /// Absolute path to the service-account JSON downloaded from the Firebase console (project
    /// settings → service accounts). Gitignored by <c>*firebase-adminsdk*.json</c> at the repo root.
    /// </summary>
    public string ServiceAccountPath { get; set; } = "";
}
