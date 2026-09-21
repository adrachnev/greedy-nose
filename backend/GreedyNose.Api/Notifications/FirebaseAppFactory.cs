using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

namespace GreedyNose.Api.Notifications;

/// <summary>
/// Loads the service-account key and turns it into the process's one <see cref="FirebaseApp"/>.
/// Called once, from the singleton registration in Program.cs, and resolved at startup — so a missing
/// or unreadable key stops the backend there, with the command that fixes it, rather than surfacing
/// as an opaque 500 on the first alert (same reasoning as <c>EnableBankingSigner</c>).
/// </summary>
public static class FirebaseAppFactory
{
    /// <summary>
    /// The credential is read and validated on every call, but the <see cref="FirebaseApp"/> is only
    /// created if the process has none yet: <c>FirebaseApp.Create</c> throws on a second default
    /// instance, which a second host in the same process (a future <c>WebApplicationFactory</c> test)
    /// would otherwise hit. The first host's app wins, and it is never disposed here — it lives as
    /// long as the process.
    /// </summary>
    public static FirebaseApp GetOrCreate(FirebaseOptions options)
    {
        var credential = LoadCredential(options.ServiceAccountPath);

        return FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions { Credential = credential });
    }

    /// <summary>
    /// Reads the key file as a service account specifically — a different kind of Google credential
    /// (a user's, say) would authenticate but could not send, and that is better learned here than as
    /// a permission error later.
    ///
    /// The error deliberately names the exception's type and never chains it: the key's private
    /// material is what a parser would be looking at when it fails, and this message goes to the
    /// console and the log.
    /// </summary>
    internal static GoogleCredential LoadCredential(string? path)
    {
        const string Hint = "Set it with `dotnet user-secrets set \"Firebase:ServiceAccountPath\" \"<path to the " +
                            "*-firebase-adminsdk-*.json downloaded from the Firebase console>\"`.";

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Firebase:ServiceAccountPath is not set. {Hint}");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Firebase service-account key not found at '{path}'. {Hint}");
        }

        try
        {
            return CredentialFactory.FromFile<ServiceAccountCredential>(path).ToGoogleCredential();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The file at '{path}' could not be read as a Firebase service-account key ({ex.GetType().Name}). " +
                "Download a fresh one from the Firebase console (Project settings → Service accounts → " +
                $"Generate new private key). {Hint}");
        }
    }
}
