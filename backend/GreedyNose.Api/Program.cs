using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.Debits;
using GreedyNose.Api.EnableBanking;
using FirebaseAdmin.Messaging;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using GreedyNose.Api.Rules;
using Microsoft.EntityFrameworkCore;

// Tracer bullet (TRACER-01-BANK-DATA.md): the thinnest path from Enable Banking to the device.
// Steps live here in order — 1 authenticate, 2 consent round trip, 3 raw transactions, 4 the
// domain mapping. The step 1-4 endpoints below still have no storage and no user. The database
// arrived with TRACER-02-NOTIFICATIONS.md step 1: each table gets its first reader or writer in
// the step that needs it — DeviceTokens in step 2, the Firebase sender in step 3, both at the end of
// this file.

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(EnableBankingOptions.SectionName).Get<EnableBankingOptions>()
              ?? new EnableBankingOptions();

// Same philosophy as the signer check below: a missing connection string is a setup mistake, and
// finding it here beats finding it as an opaque 500 on the first query. Not a connectivity check —
// a database that is merely down must not stop /health from answering.
var postgres = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgres))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not set. Configure it with `dotnet user-secrets set " +
        "\"ConnectionStrings:Postgres\" \"Host=localhost;Port=5432;Database=greedynose;Username=greedynose;Password=...\"` " +
        "(the local database is started by backend/docker-compose.yml).");
}

// A factory rather than plain AddDbContext: the ingestion worker (step 6) is a singleton
// BackgroundService and cannot take a scoped context, so it must inject
// IDbContextFactory<GreedyNoseDbContext> — never GreedyNoseDbContext itself — and make one
// short-lived context per poll tick. AddDbContextFactory also keeps GreedyNoseDbContext resolvable
// as a scoped service, so the endpoints can keep asking for it directly.
//
// No migration on startup: `dotnet ef database update` stays a deliberate, manual step.
//
// Never set `Include Error Detail` in the connection string and never call
// `EnableSensitiveDataLogging()`: either one puts the full device token (a delivery credential)
// into logs and the dev error page — see DeviceTokenEndpoint.
builder.Services.AddDbContextFactory<GreedyNoseDbContext>(db => db.UseNpgsql(postgres));

// Fail closed, same idea as the Postgres check: no key (or a short one) means no backend, not an
// open one. The message never contains the key.
var apiKey = builder.Configuration.GetSection(ApiKeyOptions.SectionName).Get<ApiKeyOptions>()
             ?? new ApiKeyOptions();
apiKey.Validate();
builder.Services.AddSingleton(apiKey);

// Scoped: a real implementation will read the current request.
builder.Services.AddScoped<ICurrentUser, OwnerCurrentUser>();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(
    builder.Configuration.GetSection(PendingConsentOptions.SectionName).Get<PendingConsentOptions>()
    ?? new PendingConsentOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EnableBankingSigner>();
builder.Services.AddSingleton(sp => new ConsentStore(
    Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, options.ConsentFilePath),
    options.ConsentValidity,
    sp.GetRequiredService<ILogger<ConsentStore>>()));
builder.Services.AddHttpClient<EnableBankingClient>(client =>
{
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Notification tracer bullet, step 3. One FirebaseApp for the process, created from the service-account
// key the first time the sender is resolved — which happens right after Build(), so a missing or
// unreadable key stops the backend at startup rather than on the first alert. Singletons on purpose:
// FirebaseMessaging holds the credential and its token cache, and the step 6 worker is a singleton too.
var firebase = builder.Configuration.GetSection(FirebaseOptions.SectionName).Get<FirebaseOptions>()
               ?? new FirebaseOptions();
builder.Services.AddSingleton(firebase);
builder.Services.AddSingleton(sp =>
    FirebaseMessaging.GetMessaging(FirebaseAppFactory.GetOrCreate(sp.GetRequiredService<FirebaseOptions>())));
builder.Services.AddSingleton<INotificationSender, FirebaseNotificationSender>();

// Notification tracer bullet, step 6: the ingestion worker. IngestionOptions has no credential —
// bound straight from config, same as the others are bound from user secrets, just a different
// source. EnableBankingDebitsFetcher is scoped-free (built fresh per call from
// IHttpClientFactory, see its own doc comment) so it can be a singleton like everything else the
// worker depends on.
var ingestion = builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()
                ?? new IngestionOptions();
builder.Services.AddSingleton(ingestion);
builder.Services.AddSingleton<IDebitsFetcher, EnableBankingDebitsFetcher>();
builder.Services.AddSingleton<ISessionStatusChecker, EnableBankingSessionStatusChecker>();
builder.Services.AddSingleton<IngestionRunner>();
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

// Everything requires the API key except the /connect and /callback browser pages (Story 1.3).
// First in the pipeline, so no endpoint — including one added later — can be reached without it.
app.UseMiddleware<ApiKeyMiddleware>();

// Fail loudly at startup rather than on the first request: a missing key or application ID is a
// setup mistake, and finding it in a 500 later costs more than finding it here.
app.Services.GetRequiredService<EnableBankingSigner>();

// Same for the Firebase key: resolving the sender loads it (see FirebaseAppFactory).
app.Services.GetRequiredService<INotificationSender>();

// Pick up a consent left by an earlier run, so restarting the backend does not cost a click.
app.Services.GetRequiredService<ConsentStore>().Restore(TimeProvider.System.GetUtcNow());

app.MapGet("/health", (ConsentStore consent, TimeProvider clock) => Results.Ok(new
{
    status = "ok",
    applicationId = options.ApplicationId,
    baseUrl = options.BaseUrl,
    connected = consent.SessionId is not null,
    // active / expired / none, judged now (R19) — `connected` only says a session id is remembered.
    state = consent.GetState(clock.GetUtcNow()).Status.ToString().ToLowerInvariant(),
    aspsp = consent.AspspName,
    account = consent.PrimaryAccount,
    connectedAt = consent.ConnectedAt,
    expiresAt = consent.ExpiresAt,
}));

// --- Step 2: consent round trip ---------------------------------------------------------------

// Story 1.4: starting a consent is protected. POST /connect-links (behind the API key) returns a
// one-time browser URL; /connect?token= spends it and redirects to the bank; the bank returns to
// /callback with a one-time state. The two pages authenticate by those secrets, not by the header.
app.MapPost("/connect-links", ConsentEndpoints.CreateLinkAsync);
app.MapGet("/connect", ConsentEndpoints.ConnectAsync);
app.MapGet("/callback", ConsentEndpoints.CallbackAsync);

// --- Step 4: the domain mapping ---------------------------------------------------------------

// What the app actually consumes: Payee and Debit exactly as app/src/domain/model.ts declares
// them. No classification travels — R6 makes that the client's job, derived from the current rule,
// which is what lets a rule edit re-label existing debits without the backend hearing about it.
// Story 1.6: served from the stored Debits/Payees; no bank call (see DebitsEndpoint).
app.MapGet("/debits", DebitsEndpoint.HandleAsync);

// --- Notification tracer bullet, step 2: device token registration ---------------------------

// The app posts its FCM token here on every launch (and when Firebase rotates it); idempotent, no
// auth yet — see DeviceTokenEndpoint for both. Takes the scoped GreedyNoseDbContext.
app.MapPost("/device-token", DeviceTokenEndpoint.HandleAsync);

// --- Notification tracer bullet, step 4: rules sync --------------------------------------------

// app/src/data/rulesStore.ts posts here after every local save (fire-and-forget, error-swallowed —
// AsyncStorage stays the source of truth per R6). Upserts Payees then Rules for the seeded user;
// see RulesEndpoint for the Rules→Payees FK ordering and the no-auth PRAGMATIC note.
app.MapPost("/rules", RulesEndpoint.HandleAsync);

app.Run();
