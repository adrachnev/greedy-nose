using System.Globalization;
using System.Text.Json;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Consent;

/// <summary>The body of <c>POST /connect-links</c>. Every field optional; defaults come from <see cref="EnableBankingOptions"/>.</summary>
public sealed record ConnectLinkRequest(string? Aspsp, string? Country, string? PsuType);

/// <summary>The answer of <c>POST /connect-links</c>: the one-time browser URL and the id of this attempt (Story 5.3), which the app uses to ask for its state.</summary>
public sealed record ConnectLinkResponse(string Url, Guid AttemptId);

/// <summary>
/// Starting a bank consent, in three steps (Story 1.4):
/// <list type="number">
/// <item><c>POST /connect-links</c> — behind the API key like every other endpoint — stores a
/// <see cref="PendingConsent"/> and returns a one-time browser URL.</item>
/// <item><c>GET /connect?token=</c> — a browser page, so no header: the token is its credential. It is
/// consumed once, mints the OAuth <c>state</c> and redirects to the bank. Without a valid token it
/// starts nothing and never calls Enable Banking.</item>
/// <item><c>GET /callback</c> — the bank sends the browser back. The <c>state</c> is its credential,
/// also single use, and is spent <em>before</em> the code is traded.</item>
/// </list>
/// Both pages answer a bad secret with the same generic 400 — unknown, used and expired are
/// indistinguishable — and encode everything they print. Raw tokens, states, codes and the session id
/// are never logged and never shown.
/// </summary>
public static class ConsentEndpoints
{
    private static readonly string Category = typeof(ConsentEndpoints).FullName!;

    // Bounds on caller-supplied text: it is stored, logged (bank name) and sent to the bank.
    private const int MaxAspspLength = 100;
    private const int MaxSecretLength = 200;
    private const int MaxLoggedErrorLength = 50;

    // The browser is waiting on the page; a slow queue must not hold it for long.
    private static readonly TimeSpan FirstSyncRequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] PsuTypes = ["personal", "business"];

    public static async Task<IResult> CreateLinkAsync(
        ConnectLinkRequest? request,
        GreedyNoseDbContext db,
        AspspResolver resolver,
        EnableBankingClient eb,
        ConsentCipher cipher,
        ConsentStore consent,
        EnableBankingOptions options,
        PendingConsentOptions pending,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var aspsp = request?.Aspsp ?? options.DefaultAspsp;
        var country = request?.Country ?? options.DefaultCountry;
        var psuType = request?.PsuType ?? "personal";

        var problems = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(aspsp) || aspsp.Length > MaxAspspLength || aspsp.Any(char.IsControl))
        {
            problems["aspsp"] = [$"Required, at most {MaxAspspLength} characters, no control characters."];
        }

        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            problems["country"] = ["Two uppercase letters, e.g. DE."];
        }

        if (!PsuTypes.Contains(psuType))
        {
            problems["psuType"] = ["Either personal or business."];
        }

        if (!Uri.TryCreate(options.RedirectUrl, UriKind.Absolute, out var redirect))
        {
            // A setup mistake, not the caller's: the link could not point anywhere.
            return Results.Problem("EnableBanking:RedirectUrl is not an absolute URL.");
        }

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        // Refuse early, before a link is spent on a bank that cannot be started. The same lookup runs
        // again when the link is used (the list is never cached).
        var found = await resolver.ResolveAsync(aspsp, country, ct);
        var refusal = found.Outcome switch
        {
            AspspLookup.NotFound => Results.Problem(
                $"No bank named \"{aspsp}\" in {country} was found in Enable Banking's list.",
                statusCode: StatusCodes.Status400BadRequest, title: "Bank not found"),
            AspspLookup.Ambiguous => Results.Problem(
                $"More than one bank named \"{aspsp}\" in {country} matches; nothing was started.",
                statusCode: StatusCodes.Status400BadRequest, title: "Bank is ambiguous"),
            AspspLookup.ListUnavailable => Results.Problem(
                "Enable Banking's bank list could not be read; nothing was started. Try again later.",
                statusCode: StatusCodes.Status502BadGateway, title: "Bank list unavailable"),
            _ => null,
        };
        if (refusal is not null)
        {
            loggers.CreateLogger(Category).LogInformation("No consent link created for {Aspsp}: {Outcome}.", aspsp, found.Outcome);
            return refusal;
        }

        var now = clock.GetUtcNow();

        // gn-4b3: a bank that is connected (or still has a live consent) is not started a second time. An expired
        // or dead consent reads Expired here, and that is the reconnect. Read from the database first: another
        // instance may have stored the consent.
        try
        {
            await consent.RestoreAsync(now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger(Category).LogError("Could not read the connections ({ExceptionType}).", ex.GetType().Name);
            return Results.Problem(title: "The connections are unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // LIMIT: compared by name only. BankConsent stores no country, so one name in several countries counts
        // as the same bank until the country is stored.
        if (consent.GetStates(now).Any(s => s.UserId == SeedData.UserId && s.Status != ConsentStatus.Expired
            && string.Equals(s.AspspName, found.Name, StringComparison.OrdinalIgnoreCase)))
        {
            loggers.CreateLogger(Category).LogInformation("No consent link created for {Aspsp}: already connected.", found.Name);
            return Results.Problem(
                $"\"{found.Name}\" is already connected; nothing was started.",
                statusCode: StatusCodes.Status409Conflict, title: "Bank already connected");
        }

        // Expired rows are dead weight (nothing can use them); clearing them here keeps the table
        // small without a background job. Small table, so load-and-remove rather than a bulk delete.
        // Story 5.3: a row that still holds a waiting session (a choice whose deadline has not passed, even if the
        // flow's own deadline has) is kept; choices that ran out are closed first, so no open session is dropped blind.
        await ConnectAttempts.CloseExpiredChoicesAsync(db, eb, cipher, now, loggers.CreateLogger(Category), ct);
        var expired = await db.PendingConsents.Where(p => p.ExpiresAt <= now && p.WaitingPayload == null).ToListAsync(ct);
        if (expired.Count > 0)
        {
            db.PendingConsents.RemoveRange(expired);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request already deleted them — the goal is met. Forget the stale entries so
                // they do not poison the save of the new row below.
                db.ChangeTracker.Clear();
            }
        }

        var token = OneTimeSecret.Generate();
        var attemptId = Guid.NewGuid();
        db.PendingConsents.Add(new PendingConsent
        {
            Id = attemptId,
            UserId = SeedData.UserId,
            // The list's own spelling, not the caller's: this string becomes the consent key, and "n26"
            // and "N26" must not become two consents for one bank.
            AspspName = found.Name,
            Country = found.Country,
            PsuType = psuType,
            LinkTokenHash = OneTimeSecret.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + pending.Lifetime,
        });
        await db.SaveChangesAsync(ct);

        loggers.CreateLogger(Category).LogInformation(
            "Consent link created for {Aspsp}, valid for {Lifetime}.", found.Name, pending.Lifetime);

        // The origin of the registered redirect URL: whatever address Enable Banking sends the
        // browser back to is, by construction, an address the owner can reach.
        var url = $"{redirect.GetLeftPart(UriPartial.Authority)}/connect?token={Uri.EscapeDataString(token)}";

        return Results.Ok(new ConnectLinkResponse(url, attemptId));
    }

    public static async Task<IResult> ConnectAsync(
        string? token,
        GreedyNoseDbContext db,
        EnableBankingClient eb,
        AspspResolver resolver,
        EnableBankingOptions options,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger(Category);

        // Nothing below runs — no database write, no call to Enable Banking — without a plausible token.
        if (string.IsNullOrEmpty(token) || token.Length > MaxSecretLength)
        {
            return Rejected("Link not valid", "This link is not valid. Go back to Greedy Nose and try again.");
        }

        var now = clock.GetUtcNow();
        var tokenHash = OneTimeSecret.Hash(token);
        var record = await db.PendingConsents.SingleOrDefaultAsync(
            p => p.LinkTokenHash == tokenHash && p.LinkUsedAt == null && p.ExpiresAt > now, ct);

        if (record is null)
        {
            return Rejected("Link not valid", "This link is not valid. Go back to Greedy Nose and try again.");
        }

        // Spend the link before anything else. The state is minted in the same write, so the callback
        // can only ever find a state that belongs to a link that was really used.
        var state = OneTimeSecret.Generate();
        record.LinkUsedAt = now;
        record.StateHash = OneTimeSecret.Hash(state);
        record.Version = unchecked(record.Version + 1);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A second request spent the same link first — it is used, which is all the loser learns.
            return Rejected("Link not valid", "This link is not valid. Go back to Greedy Nose and try again.");
        }

        // The bank's own limit, read fresh now: the link may be older than the list it was checked against.
        // A bank that is gone, ambiguous or unreadable starts nothing (the link stays spent, like any
        // other failure here).
        var bank = await resolver.ResolveAsync(record.AspspName, record.Country, ct);
        if (bank.Outcome != AspspLookup.Resolved)
        {
            logger.LogWarning("The bank could not be resolved when the link was used: {Outcome}.", bank.Outcome);
            return Rejected(
                "Could not start", "The bank connection could not be started. Go back to Greedy Nose and try again.",
                StatusCodes.Status502BadGateway);
        }

        var validity = ConsentValidity.For(options.ConsentValidity, bank.MaximumConsentValiditySeconds);
        var auth = new AuthRequest(
            new AccessRequest(
                ValidUntil: now.Add(validity).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Balances: true,
                Transactions: true),
            new AspspRef(bank.Name, bank.Country),
            state,
            options.RedirectUrl,
            record.PsuType);

        var result = await TryPostAsync(eb, "/auth", auth, logger, ct);
        var url = result is { IsSuccess: true } ? TryReadText(result.Body, "url") : null;

        if (url is null)
        {
            // The link is spent either way. Status only: their body is not ours to echo into a page.
            if (result is not null)
            {
                logger.LogWarning("Enable Banking did not start the authorization (HTTP {Status}).", result.StatusCode);
            }

            return Rejected(
                "Could not start", "The bank connection could not be started. Go back to Greedy Nose and try again.",
                StatusCodes.Status502BadGateway);
        }

        return new BrowserResult(Results.Redirect(url));
    }

    public static async Task<IResult> CallbackAsync(
        string? code,
        string? state,
        string? error,
        GreedyNoseDbContext db,
        EnableBankingClient eb,
        ConsentCipher cipher,
        ConsentStore consent,
        IFirstSyncTrigger firstSync,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger(Category);
        var now = clock.GetUtcNow();

        if (error is not null)
        {
            // The text comes from the query string, so anyone can choose it: it is never echoed and never
            // logged. Only the reason it maps to (cancelled / bank-error) is.
            var reason = string.Equals(error.Trim(), "access_denied", StringComparison.OrdinalIgnoreCase)
                ? ConnectFailure.Cancelled
                : ConnectFailure.BankError;
            logger.LogInformation("The bank returned an error to the callback ({Reason}).", reason);
            await FailByStateAsync(db, state, now, reason, logger);
            return CallbackPage(StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state.Length > MaxSecretLength)
        {
            // No code but a usable state: the attempt would wait for nothing, so it is marked.
            if (string.IsNullOrEmpty(code))
            {
                await FailByStateAsync(db, state, now, ConnectFailure.BankError, logger);
            }

            return CallbackPage(StatusCodes.Status400BadRequest);
        }

        var stateHash = OneTimeSecret.Hash(state);
        var record = await db.PendingConsents.SingleOrDefaultAsync(
            p => p.StateHash == stateHash && p.LinkUsedAt != null && p.CompletedAt == null && p.ExpiresAt > now, ct);

        if (record is null)
        {
            return CallbackPage(StatusCodes.Status400BadRequest);
        }

        // Spend the state before trading the code: a replay (or a concurrent twin) loses here and never
        // reaches Enable Banking. A failed trade below leaves it spent — the owner starts again.
        record.CompletedAt = now;
        record.Version = unchecked(record.Version + 1);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CallbackPage(StatusCodes.Status400BadRequest);
        }

        var result = await TryPostAsync(eb, "/sessions", new SessionRequest(code), logger, ct);
        if (result is not { IsSuccess: true })
        {
            if (result is not null)
            {
                logger.LogWarning("Enable Banking refused the session request (HTTP {Status}).", result.StatusCode);
            }

            await MarkAsync(db, record, ConnectOutcome.Failed, ConnectFailure.CodeNotTraded, logger);
            return CallbackPage(StatusCodes.Status502BadGateway);
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(result.Body).RootElement;
        }
        catch (JsonException)
        {
            root = default;
        }

        var sessionId = root.ValueKind == JsonValueKind.Object ? Text(root, "session_id") : null;
        if (sessionId is null)
        {
            // A 200 without a session id means the shape changed under us; the body stays out of the
            // page and the log (it may carry account data).
            logger.LogWarning("Enable Banking answered the session request without a session id.");
            await MarkAsync(db, record, ConnectOutcome.Failed, ConnectFailure.BankAnswerUnusable, logger);
            return CallbackPage(StatusCodes.Status502BadGateway);
        }

        // R15: EUR only, case-insensitive. A missing currency does not count: nothing is assumed.
        var offered = ReadAccounts(root);
        var euro = offered.Where(a => string.Equals(a.Currency?.Trim(), "EUR", StringComparison.OrdinalIgnoreCase)).ToList();
        // The product is free text from the bank (it may hold a name), so it is never logged.
        logger.LogInformation(
            "{Aspsp} offered {AccountCount} account(s) [type/usage/currency]: {Shapes}; in euro: {EuroCount}.",
            record.AspspName, offered.Count, DescribeShapes(offered), euro.Count);

        if (euro.Count == 0)
        {
            // Also an empty list. Nothing stored; the session just opened is useless, so close it (best effort).
            logger.LogWarning("{Aspsp} offered no account in euro; nothing was stored.", record.AspspName);
            await ConnectAttempts.CloseSessionAsync(eb, sessionId, record.AspspName, logger, "The new session");
            await MarkAsync(db, record, ConnectOutcome.Failed, ConnectFailure.NoSupportedAccount, logger);
            return CallbackPage(StatusCodes.Status400BadRequest);
        }

        if (euro.Count > 1)
        {
            // The owner chooses (Story 5.4). Nothing is saved as a connection; the session and the list wait
            // encrypted on the attempt until the deadline.
            try
            {
                record.WaitingPayload = WaitingPayload.Seal(cipher, record.Id, new WaitingChoice(sessionId, euro));
                record.ChoiceDeadline = now + ConnectAttempts.ChoiceLifetime;
                record.Outcome = ConnectOutcome.ChooseAccount;
                record.Version = unchecked(record.Version + 1);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Could not keep the account choice: {ExceptionType}.", ex.GetType().Name);
                db.ChangeTracker.Clear();
                await ConnectAttempts.CloseSessionAsync(eb, sessionId, record.AspspName, logger, "The new session");
                await FailByIdAsync(db, record.Id, ConnectFailure.StoreFailed, logger);
                return CallbackPage(StatusCodes.Status502BadGateway);
            }

            logger.LogInformation("{Aspsp} offered several accounts in euro; waiting for the owner's choice.", record.AspspName);
            return CallbackPage(StatusCodes.Status200OK);
        }

        List<ConnectedAccount> accounts = [euro[0]];

        // Story 4.3: the session this bank had before (same user, same bank only), read from the database before it is replaced.
        var oldSessionId = await consent.GetStoredSessionIdAsync(record.UserId, record.AspspName, ct);
        try
        {
            // Persisted with CancellationToken.None: the code is already traded, so a dropped browser connection must not lose the consent.
            await consent.CompleteAsync(record.UserId, sessionId, record.AspspName, accounts, now, ReadValidUntil(root), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. the database or the encryption fails. The code is already traded and the state spent,
            // so the owner just requests a new link. Type only: a message can carry connection details.
            logger.LogError("Could not store the consent: {ExceptionType}.", ex.GetType().Name);
            // The code is traded, so the new session is open and nothing will ever use it: close it, best effort.
            await ConnectAttempts.CloseSessionAsync(eb, sessionId, record.AspspName, logger, "The new session");
            await MarkAsync(db, record, ConnectOutcome.Failed, ConnectFailure.StoreFailed, logger);
            return CallbackPage(StatusCodes.Status502BadGateway);
        }

        logger.LogInformation("Consent completed for {Aspsp}, {AccountCount} account(s).", record.AspspName, accounts.Count);
        await MarkAsync(db, record, ConnectOutcome.Saved, null, logger);

        // Only after the consent is stored, never on a failure above. Best effort: if the request fails
        // the owner still has a working consent, and the next scheduled tick runs the same first sync.
        try
        {
            using var enqueueTimeout = new CancellationTokenSource(FirstSyncRequestTimeout);
            await firstSync.RequestAsync(enqueueTimeout.Token);
        }
        catch (Exception ex)
        {
            // Type only: a message can carry a connection string.
            logger.LogWarning(
                "The first sync could not be requested ({ExceptionType}); the next scheduled tick will run it.", ex.GetType().Name);
        }

        if (oldSessionId is not null && oldSessionId != sessionId)
        {
            await CloseOldSessionAsync(eb, oldSessionId, record.AspspName, logger);
        }

        return CallbackPage(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Records how the attempt ended. Best effort and quiet: the browser page is the same either way, and the worst
    /// outcome of a failed write is that the app keeps reading "waiting" until the attempt expires. Only short codes
    /// are written; the exception type is the only thing logged.
    /// </summary>
    private static async Task MarkAsync(
        GreedyNoseDbContext db, PendingConsent record, string outcome, string? reason, ILogger logger, DateTimeOffset? spentAt = null)
    {
        // The whole mutation, so the second try applies all of it again to a freshly read row: the spent state
        // (CompletedAt) must not be lost with the first try, or the callback could be replayed into a code trade.
        // The payload goes in the same write: no outcome other than choose-account may leave one behind.
        void Apply(PendingConsent r)
        {
            if (spentAt is { } at)
            {
                r.CompletedAt = at;
            }

            r.Outcome = outcome;
            r.FailureReason = reason;
            r.WaitingPayload = null;
            r.Version = unchecked(r.Version + 1);
        }

        // Two tries: the second starts from a fresh tracking state and a fresh read of the row (a Neon that was
        // asleep or a lost race can fail the first write); a third would only hide a database that is really down.
        var id = record.Id;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (attempt == 2)
                {
                    db.ChangeTracker.Clear();
                    record = await db.PendingConsents.SingleOrDefaultAsync(p => p.Id == id) ?? record;
                }

                Apply(record);
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not record the outcome of the attempt ({ExceptionType}), try {Attempt}.", ex.GetType().Name, attempt);
            }
        }

        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// A bank error or a cancel comes back with the state in the query: find the attempt by it and mark it failed.
    /// Without a usable state, or with one that is unknown, spent or expired, nothing is touched. The state is spent
    /// here too, so the same callback cannot be replayed into a code trade.
    /// </summary>
    private static async Task FailByStateAsync(GreedyNoseDbContext db, string? state, DateTimeOffset now, string reason, ILogger logger)
    {
        if (string.IsNullOrEmpty(state) || state.Length > MaxSecretLength)
        {
            return;
        }

        try
        {
            var stateHash = OneTimeSecret.Hash(state);
            var record = await db.PendingConsents.SingleOrDefaultAsync(
                p => p.StateHash == stateHash && p.LinkUsedAt != null && p.CompletedAt == null && p.ExpiresAt > now);
            if (record is null)
            {
                return;
            }

            await MarkAsync(db, record, ConnectOutcome.Failed, reason, logger, spentAt: now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not record the outcome of the attempt ({ExceptionType}).", ex.GetType().Name);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task FailByIdAsync(GreedyNoseDbContext db, Guid id, string reason, ILogger logger)
    {
        try
        {
            var record = await db.PendingConsents.SingleOrDefaultAsync(p => p.Id == id);
            if (record is not null)
            {
                await MarkAsync(db, record, ConnectOutcome.Failed, reason, logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not record the outcome of the attempt ({ExceptionType}).", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Story 4.3: closes the bank's previous session once the new consent is stored. Best effort — nothing here
    /// can fail the reconnect. A tick holding a pre-reconnect snapshot may see CLOSED_SESSION: IngestionRunner
    /// re-reads the stored session before it pushes, so that race does not alert.
    /// </summary>
    private static Task CloseOldSessionAsync(EnableBankingClient eb, string oldSessionId, string aspspName, ILogger logger) =>
        ConnectAttempts.CloseSessionAsync(eb, oldSessionId, aspspName, logger, "The old session");

    private static IResult Rejected(string title, string message, int status = StatusCodes.Status400BadRequest) =>
        Page(status, $"<h1>{title}</h1><p>{message}</p>");

    /// <summary>
    /// Story 5.1a: the one page every /callback outcome ends in. It names neither the bank nor the outcome
    /// (the app asks the backend for the result), and its link carries no secret, so a replayed or failed
    /// callback looks exactly like a good one. Only the HTTP status differs.
    /// </summary>
    public const string CallbackLink = "greedynose://connected";

    private static IResult CallbackPage(int status) => Page(status,
        "<h1>You can close this page.</h1><p>Greedy Nose shows the result.</p>" +
        $"<p><a href=\"{CallbackLink}\" style=\"display:inline-block;padding:12px 20px;background:#111;color:#fff;border-radius:8px;text-decoration:none\">Open Greedy Nose</a></p>" +
        $"<script>setTimeout(function(){{window.location.href=\"{CallbackLink}\";}},300);</script>");

    private static IResult Page(int status, string body) =>
        new BrowserResult(Results.Content(
            $"<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><body style=\"font-family:sans-serif\">{body}</body>",
            "text/html",
            statusCode: status));

    /// <summary>
    /// Enable Banking call that cannot throw for a network fault: null means "no answer". The secret is
    /// already spent when this runs, so a timeout must end in the same generic page as any other
    /// failure, not a 500. The request's own cancellation still propagates. Only the exception type is
    /// logged — a message can carry the URL or a body.
    /// </summary>
    private static async Task<EnableBankingResponse?> TryPostAsync(
        EnableBankingClient eb, string path, object body, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await eb.PostAsync(path, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Enable Banking could not be reached for {Path}: {ExceptionType}.", path, ex.GetType().Name);
            return null;
        }
    }

    private static List<ConnectedAccount> ReadAccounts(JsonElement root)
    {
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. accounts.EnumerateArray().Select(a => new ConnectedAccount(
            Uid: Text(a, "uid") ?? "",
            Iban: a.TryGetProperty("account_id", out var id) && id.ValueKind == JsonValueKind.Object ? Text(id, "iban") : null,
            Name: Text(a, "name"),
            Currency: Text(a, "currency"),
            // Enable Banking's own cross-session account identifier. See ConnectedAccount.Key for why
            // it is the fallback and not the primary — it hashes the IBAN we already have.
            IdentificationHash: Text(a, "identification_hash"),
            CashAccountType: Text(a, "cash_account_type"),
            Product: Text(a, "product"),
            Usage: Text(a, "usage")))];
    }

    /// <summary>
    /// Per-account type/usage/currency for the log, so a live response shows what a bank really sends.
    /// Never the IBAN, name or uid. The values come from the bank, so they are control-stripped and short.
    /// </summary>
    private static string DescribeShapes(IEnumerable<ConnectedAccount> accounts) =>
        string.Join(", ", accounts.Select(a => $"{Safe(a.CashAccountType)}/{Safe(a.Usage)}/{Safe(a.Currency)}"));

    private static string Safe(string? value) =>
        value is null ? "-" : new string([.. value.Where(c => !char.IsControl(c)).Take(MaxLoggedErrorLength)]);

    /// <summary>
    /// The consent lifetime the bank actually granted, which is not necessarily the one we asked for:
    /// ASPSPs cap it at their own maximum_consent_validity.
    /// </summary>
    private static DateTimeOffset? ReadValidUntil(JsonElement root)
    {
        if (!root.TryGetProperty("access", out var access) || access.ValueKind != JsonValueKind.Object)
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

    private static string? TryReadText(string json, string property)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.ValueKind == JsonValueKind.Object ? Text(root, property) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
