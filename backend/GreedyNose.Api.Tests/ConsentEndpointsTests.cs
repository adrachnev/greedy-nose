using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GreedyNose.Api.Consent;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 1.4: the three consent endpoints, run directly as methods against EF Core's in-memory provider
/// and a stub <see cref="HttpMessageHandler"/> in place of Enable Banking — no server, no network.
/// What the in-memory provider cannot prove — two simultaneous requests racing for one secret — is
/// left to the live check against real Postgres.
/// </summary>
public sealed class ConsentEndpointsTests : IDisposable
{
    private const string BankSessionId = "bank-session-9f3c1e77";
    private const string BankIban = "DE89370400440532013000";
    private const string BankUrl = "https://bank.example/authorize?x=1";

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"consent-ep-key-{Guid.NewGuid():N}.pem");
    private readonly string _consentPath = Path.Combine(Path.GetTempPath(), $"consent-ep-{Guid.NewGuid():N}.json");
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly EnableBankingSigner _signer;
    private readonly EnableBankingOptions _options;
    private readonly PendingConsentOptions _pending = new();
    private readonly ConsentStore _consent;
    private readonly FakeClock _clock = new(T0);
    private readonly CapturingLoggerFactory _logs = new();

    // What the next Enable Banking call answers, by path.
    private Func<string, (HttpStatusCode Status, string Body)> _bank;
    private readonly List<(string Path, string Body)> _calls = [];
    private Exception? _bankThrows;
    private Action? _beforeBankThrows;

    public ConsentEndpointsTests()
    {
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(_keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        _options = new EnableBankingOptions
        {
            ApplicationId = "test-app",
            PrivateKeyPath = _keyPath,
            RedirectUrl = "https://greedy.example.com:8443/callback",
        };
        _signer = new EnableBankingSigner(_options);
        _consent = new ConsentStore(_consentPath, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        _bank = path => path == "/auth"
            ? (HttpStatusCode.OK, $$"""{"url":"{{BankUrl}}"}""")
            : (HttpStatusCode.OK, SessionBody());
    }

    public void Dispose()
    {
        _signer.Dispose();
        File.Delete(_keyPath);
        File.Delete(_consentPath);
    }

    private static string SessionBody() => $$"""
        {"session_id":"{{BankSessionId}}",
         "access":{"valid_until":"2027-01-01T00:00:00Z"},
         "accounts":[{"uid":"uid-1","account_id":{"iban":"{{BankIban}}"},"name":"Main","currency":"EUR"}]}
        """;

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(ConsentEndpointsTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (owner._bankThrows is { } fault)
            {
                owner._calls.Add((request.RequestUri!.AbsolutePath, ""));
                owner._beforeBankThrows?.Invoke();
                throw fault;
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            owner._calls.Add((path, body));
            var (status, answer) = owner._bank(path);
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);

        public void Dispose() { }

        private sealed class Capture(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
        }
    }

    /// <summary>Stands in for losing a race: the in-memory provider never raises a concurrency conflict itself.</summary>
    private sealed class LostRaceInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("lost the race");
    }

    private GreedyNoseDbContext NewDb(bool lostRace = false)
    {
        var builder = new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(_dbName);
        if (lostRace)
        {
            builder.AddInterceptors(new LostRaceInterceptor());
        }

        return new(builder.Options);
    }

    private EnableBankingClient NewClient() =>
        new(new HttpClient(new StubHandler(this), disposeHandler: true) { BaseAddress = new Uri(_options.BaseUrl) },
            _signer, _clock);

    // --- helpers that drive the endpoints the way the real flow does ------------------------------

    private async Task<IResult> CreateLink(ConnectLinkRequest? request = null)
    {
        await using var db = NewDb();
        return await ConsentEndpoints.CreateLinkAsync(request, db, _options, _pending, _clock, _logs, CancellationToken.None);
    }

    private async Task<string> CreateToken(ConnectLinkRequest? request = null)
    {
        var ok = Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink(request));
        var query = new Uri(ok.Value!.Url).Query;
        return Uri.UnescapeDataString(query["?token=".Length..]);
    }

    private async Task<IResult> Connect(string? token, bool lostRace = false)
    {
        await using var db = NewDb(lostRace);
        return await ConsentEndpoints.ConnectAsync(token, db, NewClient(), _options, _clock, _logs, CancellationToken.None);
    }

    private async Task<IResult> Callback(string? code, string? state, string? error = null, bool lostRace = false)
    {
        await using var db = NewDb(lostRace);
        return await ConsentEndpoints.CallbackAsync(
            code, state, error, db, NewClient(), _consent, _clock, _logs, CancellationToken.None);
    }

    /// <summary>Link created and used; returns the state the bank was given.</summary>
    private async Task<string> StartedState(ConnectLinkRequest? request = null)
    {
        var token = await CreateToken(request);
        Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));
        return ReadState(_calls.Last(c => c.Path == "/auth").Body);
    }

    private static string ReadState(string authBody) =>
        JsonDocument.Parse(authBody).RootElement.GetProperty("state").GetString()!;

    private static (int Status, string Html) Page(IResult result)
    {
        var content = Assert.IsType<ContentHttpResult>(Unwrap(result));
        return (content.StatusCode ?? 200, content.ResponseContent ?? "");
    }

    private static IResult Unwrap(IResult result) => Assert.IsType<BrowserResult>(result).Inner;

    private int BankCalls => _calls.Count;

    // --- POST /connect-links ----------------------------------------------------------------------

    [Fact]
    public async Task A_link_points_at_the_origin_of_the_redirect_url_and_carries_only_a_token()
    {
        var ok = Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink());

        var uri = new Uri(ok.Value!.Url);
        Assert.Equal("https://greedy.example.com:8443", uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/connect", uri.AbsolutePath);
        Assert.StartsWith("?token=", uri.Query);
        Assert.Equal(0, BankCalls);
    }

    [Fact]
    public async Task Only_hashes_of_the_secrets_are_stored()
    {
        var token = await CreateToken();

        await using var db = NewDb();
        var row = Assert.Single(db.PendingConsents);
        Assert.Equal(OneTimeSecret.Hash(token), row.LinkTokenHash);
        Assert.NotEqual(token, row.LinkTokenHash);
        Assert.Null(row.StateHash);
        Assert.Equal(T0 + _pending.Lifetime, row.ExpiresAt);
        Assert.Equal("Mock ASPSP", row.AspspName);
    }

    [Fact]
    public async Task The_state_is_stored_only_as_a_hash_too()
    {
        var state = await StartedState();

        await using var db = NewDb();
        var row = Assert.Single(db.PendingConsents);
        Assert.Equal(OneTimeSecret.Hash(state), row.StateHash);
        Assert.NotEqual(state, row.StateHash);
    }

    [Fact]
    public async Task Each_link_has_its_own_token()
    {
        Assert.NotEqual(await CreateToken(), await CreateToken());
    }

    [Theory]
    [InlineData("Bank", "Germany", "personal")]
    [InlineData("Bank", "de", "personal")]
    [InlineData("Bank", "DE", "company")]
    [InlineData("", "DE", "personal")]
    [InlineData("Bad\nBank", "DE", "personal")]
    public async Task Invalid_input_is_refused_and_creates_nothing(string aspsp, string country, string psuType)
    {
        var result = await CreateLink(new ConnectLinkRequest(aspsp, country, psuType));

        Assert.IsType<ProblemHttpResult>(result);
        await using var db = NewDb();
        Assert.Empty(db.PendingConsents);
    }

    [Fact]
    public async Task Expired_rows_are_removed_when_a_new_link_is_created()
    {
        await CreateToken();
        _clock.Now = T0 + _pending.Lifetime;
        await CreateToken();

        await using var db = NewDb();
        var row = Assert.Single(db.PendingConsents);
        Assert.Equal(_clock.Now, row.CreatedAt);
    }

    // --- GET /connect -----------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_token_redirects_to_the_bank_with_the_bank_chosen_when_the_link_was_made()
    {
        var token = await CreateToken(new ConnectLinkRequest("N26", "DE", "personal"));

        var redirect = Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));

        Assert.Equal(BankUrl, redirect.Url);
        var call = Assert.Single(_calls);
        Assert.Equal("/auth", call.Path);
        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("N26", body.GetProperty("aspsp").GetProperty("name").GetString());
        Assert.Equal(_options.RedirectUrl, body.GetProperty("redirect_url").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task A_link_works_once_then_is_refused_without_a_second_bank_call()
    {
        var token = await CreateToken();
        Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));

        var (status, _) = Page(await Connect(token));

        Assert.Equal(400, status);
        Assert.Single(_calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task Without_a_valid_token_nothing_starts_and_the_bank_is_never_called(string? token)
    {
        await CreateToken(); // a real, unused link exists — it must stay untouched

        var (status, _) = Page(await Connect(token));

        Assert.Equal(400, status);
        Assert.Equal(0, BankCalls);
        await using var db = NewDb();
        Assert.Null(Assert.Single(db.PendingConsents).LinkUsedAt);
    }

    [Fact]
    public async Task An_expired_link_is_refused_without_a_bank_call()
    {
        var token = await CreateToken();
        _clock.Now = T0 + _pending.Lifetime;

        var (status, _) = Page(await Connect(token));

        Assert.Equal(400, status);
        Assert.Equal(0, BankCalls);
    }

    [Fact]
    public async Task Unknown_used_and_expired_links_all_get_the_same_answer()
    {
        var used = await CreateToken();
        await Connect(used);
        var expired = await CreateToken();
        _clock.Now = T0 + _pending.Lifetime;

        var unknownPage = Page(await Connect("garbage"));
        var usedPage = Page(await Connect(used));
        var expiredPage = Page(await Connect(expired));

        Assert.Equal(unknownPage, usedPage);
        Assert.Equal(unknownPage, expiredPage);
    }

    [Fact]
    public async Task When_the_bank_will_not_start_it_the_page_is_generic_and_the_link_stays_spent()
    {
        _bank = _ => (HttpStatusCode.BadRequest, """{"error":"secret-detail-from-the-bank"}""");
        var token = await CreateToken();

        var (status, html) = Page(await Connect(token));

        Assert.Equal(502, status);
        Assert.DoesNotContain("secret-detail", html);
        var (again, _) = Page(await Connect(token));
        Assert.Equal(400, again);
    }

    // --- GET /callback ----------------------------------------------------------------------------

    [Fact]
    public async Task A_started_state_completes_the_consent_with_the_bank_from_the_record()
    {
        var state = await StartedState(new ConnectLinkRequest("N26", "DE", "personal"));

        var (status, html) = Page(await Callback("the-code", state));

        Assert.Equal(200, status);
        Assert.Contains("Connected to N26. You can close this page.", html);
        Assert.Equal("N26", _consent.AspspName);
        Assert.Equal(BankSessionId, _consent.SessionId);
        Assert.Equal(BankIban, _consent.PrimaryAccount!.Iban);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), _consent.ExpiresAt);
        Assert.Equal("/sessions", _calls.Last().Path);
    }

    [Fact]
    public async Task The_success_page_shows_no_session_id_and_no_iban()
    {
        var state = await StartedState();

        var (_, html) = Page(await Callback("the-code", state));

        Assert.DoesNotContain(BankSessionId, html);
        Assert.DoesNotContain(BankIban, html);
        Assert.DoesNotContain("uid-1", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public async Task A_state_works_once_then_is_refused_without_a_second_session_request()
    {
        var state = await StartedState();
        Assert.Equal(200, Page(await Callback("the-code", state)).Status);
        var callsAfterFirst = BankCalls;

        var (status, _) = Page(await Callback("the-code", state));

        Assert.Equal(400, status);
        Assert.Equal(callsAfterFirst, BankCalls);
    }

    [Fact]
    public async Task An_unknown_state_is_refused_and_the_bank_is_not_called()
    {
        await StartedState();
        var callsBefore = BankCalls;

        var (status, _) = Page(await Callback("the-code", "never-issued"));

        Assert.Equal(400, status);
        Assert.Equal(callsBefore, BankCalls);
        Assert.Null(_consent.SessionId);
    }

    [Theory]
    [InlineData(null, "state")]
    [InlineData("code", null)]
    [InlineData("", "state")]
    [InlineData("code", "")]
    public async Task A_missing_code_or_state_is_refused(string? code, string? state)
    {
        await StartedState();
        var callsBefore = BankCalls;

        Assert.Equal(400, Page(await Callback(code, state)).Status);
        Assert.Equal(callsBefore, BankCalls);
    }

    [Fact]
    public async Task A_link_that_was_never_used_has_no_state_to_complete()
    {
        // The link exists but /connect never ran, so there is no state at all.
        await CreateToken();

        Assert.Equal(400, Page(await Callback("the-code", "anything")).Status);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task An_expired_state_is_refused_without_a_session_request()
    {
        var state = await StartedState();
        var callsBefore = BankCalls;
        _clock.Now = T0 + _pending.Lifetime;

        var (status, _) = Page(await Callback("the-code", state));

        Assert.Equal(400, status);
        Assert.Equal(callsBefore, BankCalls);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task Unknown_used_and_expired_states_all_get_the_same_answer()
    {
        var used = await StartedState();
        await Callback("the-code", used);
        var expired = await StartedState();
        _clock.Now = T0 + _pending.Lifetime;

        var unknownPage = Page(await Callback("c", "never-issued"));
        var usedPage = Page(await Callback("c", used));
        var expiredPage = Page(await Callback("c", expired));

        Assert.Equal(unknownPage, usedPage);
        Assert.Equal(unknownPage, expiredPage);
    }

    [Fact]
    public async Task A_state_belongs_to_its_own_bank_not_to_a_later_one()
    {
        var first = await StartedState(new ConnectLinkRequest("DKB", "DE", "personal"));
        await StartedState(new ConnectLinkRequest("ING", "DE", "personal"));

        await Callback("the-code", first);

        Assert.Equal("DKB", _consent.AspspName);
    }

    [Fact]
    public async Task Starting_a_consent_does_not_touch_the_live_bank_name()
    {
        var state = await StartedState(new ConnectLinkRequest("N26", "DE", "personal"));
        await Callback("c", state);

        await StartedState(new ConnectLinkRequest("DKB", "DE", "personal")); // started, never completed

        Assert.Equal("N26", _consent.AspspName);
    }

    [Fact]
    public async Task A_bank_error_is_never_echoed_and_answers_400()
    {
        var (status, html) = Page(await Callback(null, null, "<script>alert(1)</script>"));

        Assert.Equal(400, status);
        Assert.DoesNotContain("script", html);
        Assert.DoesNotContain("alert", html);
        Assert.Contains("Authorization was not completed.", html);
    }

    [Fact]
    public async Task A_bank_error_is_logged_capped_and_stripped_of_control_characters()
    {
        await Callback(null, null, "a\r\nb" + new string('x', 5000));

        var line = Assert.Single(_logs.Lines, l => l.Contains("returned an error"));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.True(line.Length < 150);
    }

    [Fact]
    public async Task A_bank_name_is_html_encoded_on_the_success_page()
    {
        var state = await StartedState(new ConnectLinkRequest("<b>Evil</b> & Co", "DE", "personal"));

        var (_, html) = Page(await Callback("the-code", state));

        Assert.DoesNotContain("<b>", html);
        Assert.Contains("Connected to &lt;b&gt;Evil&lt;/b&gt; &amp; Co. You can close this page.", html);
    }

    [Fact]
    public async Task When_the_session_request_fails_the_page_is_generic_and_no_consent_is_stored()
    {
        var state = await StartedState();
        _bank = _ => (HttpStatusCode.Unauthorized, """{"error":"secret-detail-from-the-bank"}""");

        var (status, html) = Page(await Callback("the-code", state));

        Assert.Equal(502, status);
        Assert.DoesNotContain("secret-detail", html);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task A_session_response_without_a_session_id_stores_nothing()
    {
        var state = await StartedState();
        _bank = _ => (HttpStatusCode.OK, """{"accounts":[]}""");

        Assert.Equal(502, Page(await Callback("the-code", state)).Status);
        Assert.Null(_consent.SessionId);
    }

    // --- the Version token, lost races, network faults, headers -----------------------------------

    [Fact]
    public void Version_is_a_concurrency_token_on_pending_consents()
    {
        using var db = NewDb();
        var property = db.Model.FindEntityType(typeof(PendingConsent))!.FindProperty(nameof(PendingConsent.Version))!;

        Assert.True(property.IsConcurrencyToken);
    }

    [Fact]
    public async Task Spending_the_link_and_then_the_state_each_bump_Version()
    {
        var state = await StartedState();
        await using (var db = NewDb())
        {
            Assert.Equal(1, Assert.Single(db.PendingConsents).Version);
        }

        await Callback("the-code", state);

        await using var after = NewDb();
        Assert.Equal(2, Assert.Single(after.PendingConsents).Version);
    }

    [Fact]
    public async Task Losing_the_race_for_a_link_gets_the_generic_400_and_no_bank_call()
    {
        var token = await CreateToken();
        var unknown = Page(await Connect("garbage"));

        var lost = Page(await Connect(token, lostRace: true));

        Assert.Equal(unknown, lost);
        Assert.Equal(0, BankCalls);
        await using var db = NewDb();
        Assert.Null(Assert.Single(db.PendingConsents).StateHash);
    }

    [Fact]
    public async Task Losing_the_race_for_a_state_gets_the_generic_400_and_no_session_request()
    {
        var state = await StartedState();
        var callsBefore = BankCalls;
        var unknown = Page(await Callback("c", "never-issued"));

        var lost = Page(await Callback("the-code", state, lostRace: true));

        Assert.Equal(unknown, lost);
        Assert.Equal(callsBefore, BankCalls);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task A_network_fault_starting_the_authorization_gives_the_generic_502_and_logs_only_the_type()
    {
        var token = await CreateToken();
        _bankThrows = new HttpRequestException("secret-detail https://bank.example/?x=1");

        var (status, html) = Page(await Connect(token));

        Assert.Equal(502, status);
        Assert.DoesNotContain("secret-detail", html);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("secret-detail"));
        Assert.Contains(_logs.Lines, l => l.Contains(nameof(HttpRequestException)));
        Assert.Equal(400, Page(await Connect(token)).Status); // still spent
    }

    [Fact]
    public async Task A_timeout_trading_the_code_gives_the_generic_502_and_stores_no_consent()
    {
        var state = await StartedState();
        _bankThrows = new TaskCanceledException("secret-detail timed out");

        var (status, html) = Page(await Callback("the-code", state));

        Assert.Equal(502, status);
        Assert.DoesNotContain("secret-detail", html);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("secret-detail"));
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task The_requests_own_cancellation_is_not_swallowed()
    {
        var token = await CreateToken();
        using var cts = new CancellationTokenSource();

        // Cancelled from inside the bank call, so the exception really comes out of TryPostAsync (an
        // already-cancelled token would be thrown by the database query before that).
        _beforeBankThrows = cts.Cancel;
        _bankThrows = new OperationCanceledException(cts.Token);

        await using var db = NewDb();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConsentEndpoints.ConnectAsync(token, db, NewClient(), _options, _clock, _logs, cts.Token));
    }

    [Fact]
    public async Task Cleaning_up_expired_rows_survives_a_concurrent_delete()
    {
        await CreateToken();
        _clock.Now = T0 + _pending.Lifetime;

        // The interceptor throws on every save, so this only proves the cleanup's conflict is caught:
        // the endpoint then fails on its own save of the new row, not on the cleanup.
        await using var db = NewDb(lostRace: true);
        var ex = await Record.ExceptionAsync(() =>
            ConsentEndpoints.CreateLinkAsync(null, db, _options, _pending, _clock, _logs, CancellationToken.None));

        Assert.IsType<DbUpdateConcurrencyException>(ex);
        Assert.Equal(1, db.ChangeTracker.Entries<PendingConsent>().Count(e => e.State == EntityState.Added));
    }

    [Fact]
    public async Task Browser_pages_forbid_caching_and_referrers()
    {
        var token = await CreateToken();
        var results = new[] { await Connect(token), await Connect("garbage"), await Callback("c", "never-issued") };

        foreach (var result in results)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
            context.Response.Body = new MemoryStream();
            await result.ExecuteAsync(context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        }
    }

    [Fact]
    public async Task The_redirect_keeps_its_302_and_location_inside_the_wrapper()
    {
        var token = await CreateToken();
        var result = await Connect(token);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(BankUrl, context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task A_consent_that_cannot_be_stored_gives_the_generic_502_and_logs_only_the_type()
    {
        var state = await StartedState();

        // A directory where the file should go: writing it throws UnauthorizedAccessException, which
        // ConsentStore does not swallow (it only tolerates IOException).
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"consent-ep-dir-{Guid.NewGuid():N}"));
        try
        {
            var broken = new ConsentStore(directory.FullName, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
            await using var db = NewDb();

            var result = await ConsentEndpoints.CallbackAsync(
                "the-code", state, null, db, NewClient(), broken, _clock, _logs, CancellationToken.None);

            var (status, html) = Page(result);
            Assert.Equal(502, status);
            Assert.DoesNotContain(directory.FullName, html);
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(directory.FullName));
            Assert.Contains(_logs.Lines, l => l.Contains("UnauthorizedAccessException"));
        }
        finally
        {
            directory.Delete();
        }
    }

    [Fact]
    public async Task A_link_token_presented_as_the_state_is_refused()
    {
        var token = await CreateToken();
        await Connect(token);
        var callsBefore = BankCalls;

        Assert.Equal(400, Page(await Callback("the-code", token)).Status);
        Assert.Equal(callsBefore, BankCalls);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task No_state_exists_while_the_link_is_unused()
    {
        await CreateToken();
        await Connect("garbage");

        await using var db = NewDb();
        var row = Assert.Single(db.PendingConsents);
        Assert.Null(row.StateHash);
        Assert.Null(row.LinkUsedAt);
        Assert.Equal(0, row.Version);
    }

    // --- logging ----------------------------------------------------------------------------------

    [Fact]
    public async Task No_secret_reaches_the_log()
    {
        var token = await CreateToken();
        await Connect(token);
        var state = ReadState(_calls.Last(c => c.Path == "/auth").Body);
        await Callback("the-code-123", state);
        await Callback("the-code-123", state); // refused replay
        await Connect(token);                  // refused replay

        var everything = string.Join('\n', _logs.Lines);
        Assert.NotEmpty(_logs.Lines);
        foreach (var secret in new[]
                 {
                     token, state, "the-code-123", BankSessionId,
                     OneTimeSecret.Hash(token), OneTimeSecret.Hash(state), OneTimeSecret.Hash(BankSessionId), BankIban,
                 })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }
}
