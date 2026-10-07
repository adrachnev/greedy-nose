using Microsoft.Extensions.Configuration;

namespace GreedyNose.Functions;

public static class DevelopmentConfiguration
{
    /// <summary>
    /// Adds the dev user-secrets file and then the environment variables again, so an environment
    /// variable wins over a secret (as in the API host). Without the second call a secret added last
    /// would override what a container passes in.
    /// </summary>
    public static IConfigurationBuilder AddUserSecretsBelowEnvironment(this IConfigurationBuilder builder) =>
        builder.AddUserSecrets<IngestionTimerFunction>(optional: true).AddEnvironmentVariables();
}
