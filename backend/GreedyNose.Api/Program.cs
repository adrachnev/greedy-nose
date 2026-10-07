using GreedyNose.Api;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Debits;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Notifications;
using GreedyNose.Api.Rules;

// Tracer bullet (TRACER-01-BANK-DATA.md): the thinnest path from Enable Banking to the device.
// Steps live here in order — 1 authenticate, 2 consent round trip, 3 raw transactions, 4 the
// domain mapping. The step 1-4 endpoints below still have no storage and no user. The database
// arrived with TRACER-02-NOTIFICATIONS.md step 1: each table gets its first reader or writer in
// the step that needs it — DeviceTokens in step 2, the Firebase sender in step 3.
// Everything that is not HTTP (database, consent, Enable Banking, Firebase, the ingestion tick's
// services) is registered in GreedyNoseCoreServices, shared with the Functions host (Story 2.3).

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(EnableBankingOptions.SectionName).Get<EnableBankingOptions>()
              ?? new EnableBankingOptions();

// Fail closed: no key (or a short one) means no backend, not an open one. The message never
// contains the key. HTTP-only, so it lives here and not in the shared registration.
var apiKey = builder.Configuration.GetSection(ApiKeyOptions.SectionName).Get<ApiKeyOptions>()
             ?? new ApiKeyOptions();
apiKey.Validate();
builder.Services.AddSingleton(apiKey);

// Scoped: a real implementation will read the current request.
builder.Services.AddScoped<ICurrentUser, OwnerCurrentUser>();

builder.Services.AddGreedyNoseCore(builder.Configuration);

var app = builder.Build();

// Everything requires the API key except the /connect and /callback browser pages (Story 1.3).
// First in the pipeline, so no endpoint — including one added later — can be reached without it.
app.UseMiddleware<ApiKeyMiddleware>();

// Fail loudly at startup (signer, Firebase key, consent restore) — see GreedyNoseCoreServices.
await app.Services.RunGreedyNoseStartupChecksAsync();

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
