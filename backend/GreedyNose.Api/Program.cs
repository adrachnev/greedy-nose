using System.Text.Json;
using GreedyNose.Api.EnableBanking;

// Tracer bullet (TRACER-BULLET.md): the thinnest path from Enable Banking to the device.
// Steps live here in order — 1 authenticate, 2 consent round trip, 3 raw transactions. There is
// no storage, no user, no domain mapping yet; each arrives with the step that needs it.

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(EnableBankingOptions.SectionName).Get<EnableBankingOptions>()
              ?? new EnableBankingOptions();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EnableBankingSigner>();
builder.Services.AddSingleton<ConsentStore>();
builder.Services.AddHttpClient<EnableBankingClient>(client =>
{
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Fail loudly at startup rather than on the first request: a missing key or application ID is a
// setup mistake, and finding it in a 500 later costs more than finding it here.
app.Services.GetRequiredService<EnableBankingSigner>();

app.MapGet("/health", (ConsentStore consent) => Results.Ok(new
{
    status = "ok",
    applicationId = options.ApplicationId,
    baseUrl = options.BaseUrl,
    connected = consent.SessionId is not null,
    aspsp = consent.AspspName,
    account = consent.PrimaryAccount,
    connectedAt = consent.ConnectedAt,
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

    var url = JsonDocument.Parse(result.Body).RootElement.GetProperty("url").GetString();

    return url is null
        ? Results.Problem("Enable Banking returned no authorization URL.")
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
    var sessionId = root.GetProperty("session_id").GetString() ?? "";
    var accounts = ReadAccounts(root);

    consent.Complete(sessionId, accounts, clock.GetUtcNow());

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
    if (dateFrom is not null) query.Add($"date_from={dateFrom}");
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
        Currency: Text(a, "currency")))];
}

static string? Text(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
