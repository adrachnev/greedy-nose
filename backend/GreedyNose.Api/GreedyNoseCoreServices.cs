using FirebaseAdmin.Messaging;
using GreedyNose.Api.Alerts;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api;

/// <summary>
/// The non-HTTP service registration of the Functions host (Stories 2.3, 2.4): one place for the
/// services every Function uses and for the startup checks. Nothing here knows about HTTP or the API key.
/// </summary>
public static class GreedyNoseCoreServices
{
    public static IServiceCollection AddGreedyNoseCore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(EnableBankingOptions.SectionName).Get<EnableBankingOptions>()
                      ?? new EnableBankingOptions();

        // A missing connection string is a setup mistake, and finding it here beats finding it as an
        // opaque 500 on the first query. Not a connectivity check — a database that is merely down
        // must not stop /health from answering.
        var postgres = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgres))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not set. Configure it with `dotnet user-secrets set " +
                "\"ConnectionStrings:Postgres\" \"Host=localhost;Port=5432;Database=greedynose;Username=greedynose;Password=...\"` " +
                "(the local database is started by backend/docker-compose.yml).");
        }

        // A factory rather than plain AddDbContext: the ingestion tick is not a request and cannot
        // take a scoped context, so it injects IDbContextFactory<GreedyNoseDbContext> — never
        // GreedyNoseDbContext itself — and makes one short-lived context per tick.
        // AddDbContextFactory also keeps GreedyNoseDbContext resolvable as a scoped service, so the
        // endpoints can keep asking for it directly.
        //
        // No migration on startup: `dotnet ef database update` stays a deliberate, manual step.
        //
        // Never set `Include Error Detail` in the connection string and never call
        // `EnableSensitiveDataLogging()`: either one puts the full device token (a delivery credential)
        // into logs and the dev error page — see DeviceTokenEndpoint.
        services.AddDbContextFactory<GreedyNoseDbContext>(db => db.UseNpgsql(postgres));

        // Same for the consent encryption key (Story 1.7): stored consents are unreadable without it,
        // and there is no default. The message never contains the key.
        var consentKey = (configuration.GetSection(ConsentEncryptionOptions.SectionName).Get<ConsentEncryptionOptions>()
                          ?? new ConsentEncryptionOptions()).Validate();
        services.AddSingleton(new ConsentCipher(consentKey));
        services.AddSingleton<IConsentPersistence, DbConsentPersistence>();

        services.AddSingleton(options);
        services.AddSingleton(
            configuration.GetSection(PendingConsentOptions.SectionName).Get<PendingConsentOptions>()
            ?? new PendingConsentOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EnableBankingSigner>();
        services.AddSingleton(sp => new ConsentStore(
            sp.GetRequiredService<IConsentPersistence>(),
            options.ConsentValidity,
            sp.GetRequiredService<ILogger<ConsentStore>>()));
        services.AddHttpClient<EnableBankingClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // One FirebaseApp for the process, created from the service-account key the first time the
        // sender is resolved — which RunGreedyNoseKeyChecks does right after Build(), so a
        // missing or unreadable key stops the host at startup rather than on the first alert.
        // Singletons on purpose: FirebaseMessaging holds the credential and its token cache.
        var firebase = configuration.GetSection(FirebaseOptions.SectionName).Get<FirebaseOptions>()
                       ?? new FirebaseOptions();
        services.AddSingleton(firebase);
        services.AddSingleton(sp =>
            FirebaseMessaging.GetMessaging(FirebaseAppFactory.GetOrCreate(sp.GetRequiredService<FirebaseOptions>())));
        services.AddSingleton<INotificationSender, FirebaseNotificationSender>();

        // The ingestion tick. IngestionOptions has no credential — bound straight from config.
        // EnableBankingDebitsFetcher builds its client fresh per call from IHttpClientFactory (see
        // its doc comment), so it can be a singleton like everything else the tick depends on.
        var ingestion = configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()
                        ?? new IngestionOptions();
        services.AddSingleton(ingestion);
        // Operator mail (Story 2.10). Only bound here; the required-settings check runs in
        // RunGreedyNoseKeyChecks, like the Firebase key, and its message never contains a value.
        services.AddSingleton(configuration.GetSection(BrevoOptions.SectionName).Get<BrevoOptions>() ?? new BrevoOptions());
        services.AddSingleton(configuration.GetSection(AlertOptions.SectionName).Get<AlertOptions>() ?? new AlertOptions());
        services.AddHttpClient(BrevoAlertMailSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.brevo.com");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<IAlertMailSender, BrevoAlertMailSender>();
        services.AddSingleton<PushFailureWatch>();

        services.AddSingleton<IDebitsFetcher, EnableBankingDebitsFetcher>();
        services.AddSingleton<ISessionStatusChecker, EnableBankingSessionStatusChecker>();
        services.AddSingleton<IngestionRunner>();
        services.AddSingleton<IIngestionTick>(sp => sp.GetRequiredService<IngestionRunner>());

        return services;
    }

    /// <summary>
    /// Fail loudly at startup rather than on the first request or tick: a missing key or application
    /// ID is a setup mistake, and finding it later costs more than finding it here. Needs no database.
    /// Run once, right after the host is built.
    /// </summary>
    public static void RunGreedyNoseKeyChecks(this IServiceProvider services)
    {
        services.GetRequiredService<EnableBankingSigner>();

        // Resolving the sender loads the Firebase key (see FirebaseAppFactory).
        services.GetRequiredService<INotificationSender>();

        // The operator mail is required (Story 2.10): a host that could not report a dead push channel
        // should not start. Names the missing settings, never their values.
        AlertOptionsValidation.Validate(
            services.GetRequiredService<BrevoOptions>(), services.GetRequiredService<AlertOptions>());
    }
}
