using System.Globalization;
using System.Text.Json;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using FirebaseAdmin.Messaging;
using GreedyNose.Api.Notifications;
using GreedyNose.Api.Rules;
using Microsoft.EntityFrameworkCore;

// Tracer bullet (TRACER-BULLET.md): the thinnest path from Enable Banking to the device.
// Steps live here in order — 1 authenticate, 2 consent round trip, 3 raw transactions, 4 the
// domain mapping. The step 1-4 endpoints below still have no storage and no user. The database
// arrived with NOTIFICATION-TRACER-BULLET.md step 1: each table gets its first reader or writer in
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

builder.Services.AddSingleton(options);
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

var app = builder.Build();

// Fail loudly at startup rather than on the first request: a missing key or application ID is a
// setup mistake, and finding it in a 500 later costs more than finding it here.
app.Services.GetRequiredService<EnableBankingSigner>();

// Same for the Firebase key: resolving the sender loads it (see FirebaseAppFactory).
app.Services.GetRequiredService<INotificationSender>();

// Pick up a consent left by an earlier run, so restarting the backend does not cost a click.
app.Services.GetRequiredService<ConsentStore>().Restore(TimeProvider.System.GetUtcNow());

app.MapGet("/health", (ConsentStore consent) => Results.Ok(new
{
    status = "ok",
    applicationId = options.ApplicationId,
    baseUrl = options.BaseUrl,
    connected = consent.SessionId is not null,
    aspsp = consent.AspspName,
    account = consent.PrimaryAccount,
    connectedAt = consent.ConnectedAt,
    expiresAt = consent.ExpiresAt,
}));

// --- Step 1: the first authenticated call -----------------------------------------------------

app.MapGet("/aspsps", async (EnableBankingClient eb, string? country, CancellationToken ct) =>
{
    var result = await eb.GetAsync($"/aspsps?country={country ?? options.DefaultCountry}", ct);

    return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

// --- Step 2: consent round trip ---------------------------------------------------------------

// Open this in a browser. It starts authorization and forwards to the bank's own consent page;
// the bank sends the user back to /callback with a code.
app.MapGet("/connect", async (
    EnableBankingClient eb,
    ConsentStore consent,
    TimeProvider clock,
    string? aspsp,
    string? country,
    string? psuType,
    CancellationToken ct) =>
{
    var bank = aspsp ?? options.DefaultAspsp;
    var state = consent.StartAuthorization(bank);

    var request = new AuthRequest(
        new AccessRequest(
            ValidUntil: clock.GetUtcNow().Add(options.ConsentValidity).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Balances: true,
            Transactions: true),
        new AspspRef(bank, country ?? options.DefaultCountry),
        state,
        options.RedirectUrl,
        psuType ?? "personal");

    var result = await eb.PostAsync("/auth", request, ct);
    if (!result.IsSuccess)
    {
        return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
    }

    var url = Text(JsonDocument.Parse(result.Body).RootElement, "url");

    return url is null
        ? Results.Content(result.Body, "application/json", statusCode: 502)
        : Results.Redirect(url);
});

// Where the bank sends the user back. Trades the code for a session and remembers the account.
app.MapGet("/callback", async (
    EnableBankingClient eb,
    ConsentStore consent,
    TimeProvider clock,
    string? code,
    string? state,
    string? error,
    CancellationToken ct) =>
{
    if (error is not null)
    {
        return Html($"<h1>Authorization failed</h1><p>The bank returned: <code>{error}</code></p>");
    }

    if (code is null)
    {
        return Html("<h1>No code</h1><p>The redirect carried no <code>code</code> parameter.</p>");
    }

    // The state proves this redirect belongs to the authorization we started, not to a stray link.
    if (!consent.IsExpectedState(state))
    {
        return Html("<h1>Unexpected state</h1><p>Start again at <a href=\"/connect\">/connect</a>.</p>");
    }

    var result = await eb.PostAsync("/sessions", new SessionRequest(code), ct);
    if (!result.IsSuccess)
    {
        return Html($"<h1>Session failed ({result.StatusCode})</h1><pre>{result.Body}</pre>");
    }

    var root = JsonDocument.Parse(result.Body).RootElement;
    var sessionId = Text(root, "session_id");
    if (sessionId is null)
    {
        // A 200 without a session id means the shape changed under us. Show what they sent rather
        // than throwing: their body is the only thing that explains it.
        return Html($"<h1>No session id in the response</h1><pre>{result.Body}</pre>");
    }

    var accounts = ReadAccounts(root);

    consent.Complete(sessionId, accounts, clock.GetUtcNow(), ReadValidUntil(root));

    var rows = string.Concat(accounts.Select(a =>
        $"<tr><td><code>{a.Uid}</code></td><td>{a.Iban}</td><td>{a.Name}</td><td>{a.Currency}</td></tr>"));

    return Html($"""
        <h1>Connected</h1>
        <p>Session <code>{sessionId}</code> at <b>{consent.AspspName}</b>.</p>
        <table border="1" cellpadding="6" cellspacing="0">
          <tr><th>account uid</th><th>iban</th><th>name</th><th>currency</th></tr>
          {rows}
        </table>
        <p>Next: <a href="/raw">/raw</a> — the transactions, exactly as the bank sends them.</p>
        """);
});

// --- Step 3: raw transactions -----------------------------------------------------------------

// Returned verbatim and written to disk. The file is the evidence R3a and R10b/R10c are supposed
// to be re-tuned against (REQUIREMENTS.md, "Refining these from real data") — parsing comes next
// in step 4, and guessing at field shapes before seeing them is what this step exists to avoid.
app.MapGet("/raw", async (
    EnableBankingClient eb,
    ConsentStore consent,
    TimeProvider clock,
    IWebHostEnvironment env,
    string? dateFrom,
    string? continuationKey,
    CancellationToken ct) =>
{
    var account = consent.PrimaryAccount;
    if (account is null)
    {
        return Results.Problem("No connected account. Visit /connect first.", statusCode: 409);
    }

    var query = new List<string>();
    if (dateFrom is not null) query.Add($"date_from={Uri.EscapeDataString(dateFrom)}");
    if (continuationKey is not null) query.Add($"continuation_key={Uri.EscapeDataString(continuationKey)}");
    var suffix = query.Count > 0 ? "?" + string.Join("&", query) : "";

    var result = await eb.GetAsync($"/accounts/{account.Uid}/transactions{suffix}", ct);

    var directory = Path.Combine(env.ContentRootPath, options.RawDumpDirectory);
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, $"transactions-{clock.GetUtcNow():yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(file, result.Body, ct);
    app.Logger.LogInformation("Raw transactions written to {File}", file);

    return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
});

// --- Step 4: the domain mapping ---------------------------------------------------------------

// What the app actually consumes: Payee and Debit exactly as app/src/domain/model.ts declares
// them. No classification travels — R6 makes that the client's job, derived from the current rule,
// which is what lets a rule edit re-label existing debits without the backend hearing about it.
app.MapGet("/debits", async (EnableBankingClient eb, ConsentStore consent, CancellationToken ct) =>
{
    var account = consent.PrimaryAccount;
    if (account is null)
    {
        return Results.Problem("No connected account. Visit /connect first.", statusCode: 409);
    }

    var result = await eb.GetAsync($"/accounts/{account.Uid}/transactions", ct);
    if (!result.IsSuccess)
    {
        return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
    }

    var response = JsonSerializer.Deserialize<EbTransactionsResponse>(result.Body, EnableBankingClient.Json);
    if (response is null)
    {
        return Results.Problem("Enable Banking returned a body we could not read.");
    }

    // Account.Key, never Account.Uid — the uid changes every session, and debit identity must not.
    var mapped = TransactionMapper.Map(response, account.Key);

    // Loudly, one line each: a charge the mapper could not represent is invisible to the user, and
    // an invisible charge is the failure R1 exists to prevent. Step 7 wants these counts.
    //
    // Only the *count* goes to the app, as DebitsDto.Skipped — these strings name merchants and
    // amounts, and the app is not their audience. The mapper fills that count itself, so the
    // payload here is already complete; nothing below patches it.
    foreach (var skipped in mapped.Skipped)
    {
        app.Logger.LogWarning("Skipped a debit the mapper could not represent — {Detail}", skipped);
    }

    return Results.Ok(mapped.Payload);
});

// --- Notification tracer bullet, step 2: device token registration ---------------------------

// The app posts its FCM token here on every launch (and when Firebase rotates it); idempotent, no
// auth yet — see DeviceTokenEndpoint for both. Takes the scoped GreedyNoseDbContext.
app.MapPost("/device-token", DeviceTokenEndpoint.HandleAsync);

// --- Notification tracer bullet, step 3: the backend can send ---------------------------------

// TRACER-BULLET: proves the send path before the worker exists — delete once step 6 proves the real
// path. Development only, because it pushes to the owner's real phone on demand and has no auth.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/debug/send-test-push", DebugSendTestPushEndpoint.HandleAsync);
}

// --- Notification tracer bullet, step 4: rules sync --------------------------------------------

// app/src/data/rulesStore.ts posts here after every local save (fire-and-forget, error-swallowed —
// AsyncStorage stays the source of truth per R6). Upserts Payees then Rules for the seeded user;
// see RulesEndpoint for the Rules→Payees FK ordering and the no-auth PRAGMATIC note.
app.MapPost("/rules", RulesEndpoint.HandleAsync);

app.Run();

static IResult Html(string body) =>
    Results.Content($"<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif\">{body}</body>",
        "text/html");

static List<ConnectedAccount> ReadAccounts(JsonElement root)
{
    if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
    {
        return [];
    }

    return [.. accounts.EnumerateArray().Select(a => new ConnectedAccount(
        Uid: Text(a, "uid") ?? "",
        Iban: a.TryGetProperty("account_id", out var id) ? Text(id, "iban") : null,
        Name: Text(a, "name"),
        Currency: Text(a, "currency"),
        // Enable Banking's own cross-session account identifier. See ConnectedAccount.Key for why
        // it is the fallback and not the primary — it hashes the IBAN we already have.
        IdentificationHash: Text(a, "identification_hash")))];
}

/// <summary>
/// The consent lifetime the bank actually granted, which is not necessarily the one we asked for:
/// ASPSPs cap it at their own maximum_consent_validity.
/// </summary>
static DateTimeOffset? ReadValidUntil(JsonElement root)
{
    if (!root.TryGetProperty("access", out var access))
    {
        return null;
    }

    return DateTimeOffset.TryParse(
        Text(access, "valid_until"),
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal,
        out var validUntil)
        ? validUntil
        : null;
}

static string? Text(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
