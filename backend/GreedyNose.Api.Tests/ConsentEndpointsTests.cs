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
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly EnableBankingSigner _signer;
    private readonly EnableBankingOptions _options;
    private readonly PendingConsentOptions _pending = new();
    private readonly ConsentStore _consent;
    private readonly FakeClock _clock = new(T0);
    private readonly CapturingLoggerFactory _logs = new();
    private readonly FakeFirstSync _firstSync = new();

    // What the next Enable Banking call answers, by path.
    private Func<string, (HttpStatusCode Status, string Body)> _bank;
    private readonly List<(string Path, string Body)> _calls = [];
    private Exception? _bankThrows;
    private Action? _beforeBankThrows;

    // Story 4.3: the session id the next POST /sessions hands out, and DELETE /sessions/{id} answered apart from _calls
    // (each DELETE also records which session the store held at that moment, to prove "stored before closed").
    private string _sessionId = BankSessionId;
    private (HttpStatusCode Status, string Body) _deleteAnswer = (HttpStatusCode.NoContent, "");
    private Exception? _deleteThrows;
    private readonly List<(string Path, string? StoredSessionAtCall)> _deletes = [];

    // GET /aspsps is answered apart from _bank and counted apart from _calls, so the existing
    // "how many bank calls" assertions keep meaning "calls that start or complete a consent".
    private (HttpStatusCode Status, string Body) _aspsps = (HttpStatusCode.OK, AspspList());
    private readonly List<string> _aspspQueries = [];
    private Exception? _aspspThrows;

    private static string AspspList(string? bankMax = "15552000", string name = "Mock ASPSP") => $$"""
        {"aspsps":[
          {"name":"{{name}}","country":"DE","maximum_consent_validity":{{bankMax ?? "null"}}},
          {"name":"N26","country":"DE","maximum_consent_validity":15552000},
          {"name":"DKB","country":"DE"},
          {"name":"ING","country":"DE","maximum_consent_validity":15552000},
          {"name":"N26","country":"FR"},
          {"name":"<b>Evil</b> & Co","country":"DE"}]}
        """;

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
        _consent = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        _bank = path => path == "/auth"
            ? (HttpStatusCode.OK, $$"""{"url":"{{BankUrl}}"}""")
            : (HttpStatusCode.OK, SessionBody());
    }

    public void Dispose()
    {
        _signer.Dispose();
        File.Delete(_keyPath);
    }

    private string SessionBody() => $$"""
        {"session_id":"{{_sessionId}}",
         "access":{"valid_until":"2027-01-01T00:00:00Z"},
         "accounts":[{"uid":"uid-1","account_id":{"iban":"{{BankIban}}"},"name":"Main","currency":"EUR"}]}
        """;

    private sealed class FakeFirstSync : IFirstSyncTrigger
    {
        public int Requests;
        public Exception? Throws;

        public Task RequestAsync(CancellationToken ct)
        {
            Requests++;
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(ConsentEndpointsTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                owner._deletes.Add((path, owner._consent.SessionId));
                if (owner._deleteThrows is { } deleteFault)
                {
                    throw deleteFault;
                }

                return new HttpResponseMessage(owner._deleteAnswer.Status) { Content = new StringContent(owner._deleteAnswer.Body) };
            }

            var isList = path == "/aspsps";
            if (isList)
            {
                owner._aspspQueries.Add(request.RequestUri.Query);
            }

            if ((isList ? owner._aspspThrows : owner._bankThrows) is { } fault)
            {
                if (!isList)
                {
                    owner._calls.Add((path, ""));
                }

                owner._beforeBankThrows?.Invoke();
                throw fault;
            }

            if (isList)
            {
                return new HttpResponseMessage(owner._aspsps.Status) { Content = new StringContent(owner._aspsps.Body) };
            }

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
        return await ConsentEndpoints.CreateLinkAsync(
            request, db, new AspspResolver(NewClient()), _options, _pending, _clock, _logs, CancellationToken.None);
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
        var client = NewClient();
        return await ConsentEndpoints.ConnectAsync(
            token, db, client, new AspspResolver(client), _options, _clock, _logs, CancellationToken.None);
    }

    private async Task<IResult> Callback(string? code, string? state, string? error = null, bool lostRace = false)
    {
        await using var db = NewDb(lostRace);
        return await ConsentEndpoints.CallbackAsync(
            code, state, error, db, NewClient(), _consent, _firstSync, _clock, _logs, CancellationToken.None);
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

    // --- Story 3.3: the bank is looked up in Enable Banking's list, and its own limit is used ----------

    private AspspResolver NewResolver() => new(NewClient());

    [Theory]
    [InlineData("N26", "DE", "N26")]
    [InlineData("n26", "de", "N26")]
    [InlineData("MOCK aspsp", "DE", "Mock ASPSP")]
    public async Task The_resolver_matches_name_and_country_exactly_ignoring_case(string name, string country, string expected)
    {
        var found = await NewResolver().ResolveAsync(name, country, CancellationToken.None);

        Assert.Equal(AspspLookup.Resolved, found.Outcome);
        Assert.Equal(expected, found.Name);
        Assert.Equal("?country=" + country, Assert.Single(_aspspQueries));
    }

    [Theory]
    [InlineData("N2", "DE")]        // a prefix is not a match
    [InlineData("N26 Bank", "DE")]
    [InlineData("DKB", "FR")]       // right name, wrong country
    [InlineData("No Such Bank", "DE")]
    public async Task The_resolver_finds_nothing_for_a_near_miss(string name, string country)
    {
        Assert.Equal(AspspLookup.NotFound, (await NewResolver().ResolveAsync(name, country, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_resolver_does_not_guess_between_two_matches()
    {
        _aspsps = (HttpStatusCode.OK, """{"aspsps":[{"name":"Twin","country":"DE"},{"name":"twin","country":"DE"}]}""");

        Assert.Equal(AspspLookup.Ambiguous, (await NewResolver().ResolveAsync("Twin", "DE", CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"aspsps":[]}""")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, """{"something":"else"}""")]
    [InlineData(HttpStatusCode.OK, """[1,2]""")]
    public async Task The_resolver_reports_an_unreadable_list(HttpStatusCode status, string body)
    {
        _aspsps = (status, body);

        Assert.Equal(AspspLookup.ListUnavailable, (await NewResolver().ResolveAsync("N26", "DE", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_resolver_reports_a_network_fault_or_timeout_as_unavailable()
    {
        _aspspThrows = new HttpRequestException("secret-detail");
        Assert.Equal(AspspLookup.ListUnavailable, (await NewResolver().ResolveAsync("N26", "DE", CancellationToken.None)).Outcome);

        _aspspThrows = new TaskCanceledException("secret-detail");
        Assert.Equal(AspspLookup.ListUnavailable, (await NewResolver().ResolveAsync("N26", "DE", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_missing_maximum_is_null_and_a_stated_one_is_read_in_seconds()
    {
        var stated = await NewResolver().ResolveAsync("N26", "DE", CancellationToken.None);
        var absent = await NewResolver().ResolveAsync("DKB", "DE", CancellationToken.None);

        Assert.Equal(15552000L, stated.MaximumConsentValiditySeconds);
        Assert.Null(absent.MaximumConsentValiditySeconds);
    }

    [Theory]
    [InlineData(2592000L, 30)]      // the bank allows less than 90 days: respected
    [InlineData(7776000L, 90)]      // exactly 90 days
    [InlineData(15552000L, 90)]     // 180 days: 90 wins
    [InlineData(null, 90)]          // not stated
    [InlineData(0L, 90)]            // non-positive counts as not stated
    [InlineData(-5L, 90)]
    public void The_requested_validity_is_the_smaller_of_ninety_days_and_the_banks_maximum(long? max, int expectedDays)
    {
        Assert.Equal(TimeSpan.FromDays(expectedDays), ConsentValidity.For(TimeSpan.FromDays(90), max));
    }

    [Fact]
    public void A_huge_maximum_does_not_overflow()
    {
        Assert.Equal(TimeSpan.FromDays(90), ConsentValidity.For(TimeSpan.FromDays(90), long.MaxValue));
    }

    [Theory]
    [InlineData("No Such Bank", "DE", HttpStatusCode.OK, "{\"aspsps\":[]}", 400)]
    [InlineData("Twin", "DE", HttpStatusCode.OK, "{\"aspsps\":[{\"name\":\"Twin\",\"country\":\"DE\"},{\"name\":\"Twin\",\"country\":\"DE\"}]}", 400)]
    [InlineData("N26", "DE", HttpStatusCode.InternalServerError, "oops secret-detail", 502)]
    [InlineData("N26", "DE", HttpStatusCode.OK, "garbage", 502)]
    public async Task A_link_for_an_unknown_ambiguous_or_unlistable_bank_is_refused_and_creates_nothing(
        string aspsp, string country, HttpStatusCode listStatus, string listBody, int expectedStatus)
    {
        _aspsps = (listStatus, listBody);

        var result = Assert.IsType<ProblemHttpResult>(await CreateLink(new ConnectLinkRequest(aspsp, country, null)));

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.DoesNotContain("secret-detail", result.ProblemDetails.Detail);
        Assert.False(string.IsNullOrWhiteSpace(result.ProblemDetails.Detail));
        await using var db = NewDb();
        Assert.Empty(db.PendingConsents);
        Assert.Equal(0, BankCalls);
    }

    [Fact]
    public async Task A_refusal_names_the_bank_and_country()
    {
        var result = Assert.IsType<ProblemHttpResult>(await CreateLink(new ConnectLinkRequest("No Such Bank", "DE", null)));

        Assert.Contains("No Such Bank", result.ProblemDetails.Detail);
        Assert.Contains("DE", result.ProblemDetails.Detail);
    }

    [Fact]
    public async Task A_link_for_a_listed_bank_is_created_after_one_list_read_for_its_country()
    {
        await CreateToken(new ConnectLinkRequest("N26", "DE", "personal"));

        Assert.Equal("?country=DE", Assert.Single(_aspspQueries));
    }

    private JsonElement StartedAuthRequest() =>
        JsonDocument.Parse(Assert.Single(_calls, c => c.Path == "/auth").Body).RootElement;

    [Theory]
    [InlineData("2592000", 30)]    // 30 days: smaller than 90, respected
    [InlineData("15552000", 90)]   // 180 days: 90 wins
    [InlineData("7776000", 90)]    // exactly 90
    [InlineData(null, 90)]         // not stated
    [InlineData("0", 90)]
    [InlineData("-100", 90)]
    public async Task The_authorization_asks_for_the_smaller_of_ninety_days_and_the_banks_maximum(string? bankMax, int expectedDays)
    {
        _aspsps = (HttpStatusCode.OK, AspspList(bankMax));
        var token = await CreateToken();

        Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));

        var validUntil = StartedAuthRequest().GetProperty("access").GetProperty("valid_until").GetString();
        Assert.Equal(T0.AddDays(expectedDays).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), validUntil);
    }

    [Fact]
    public async Task The_banks_own_spelling_is_stored_sent_to_the_bank_and_becomes_the_consent_name()
    {
        var state = await StartedState(new ConnectLinkRequest("MOCK aspsp", "DE", null));

        await using (var db = NewDb())
        {
            var row = Assert.Single(db.PendingConsents);
            Assert.Equal("Mock ASPSP", row.AspspName);
            Assert.Equal("DE", row.Country);
        }

        var aspsp = StartedAuthRequest().GetProperty("aspsp");
        Assert.Equal("Mock ASPSP", aspsp.GetProperty("name").GetString());
        Assert.Equal("DE", aspsp.GetProperty("country").GetString());

        var (_, html) = Page(await Callback("the-code", state));
        Assert.Contains("Connected to Mock ASPSP.", html);
        Assert.Equal("Mock ASPSP", _consent.AspspName);
    }

    [Fact]
    public async Task A_lowercase_n26_is_stored_as_N26()
    {
        await CreateToken(new ConnectLinkRequest("n26", "DE", null));

        await using var db = NewDb();
        Assert.Equal("N26", Assert.Single(db.PendingConsents).AspspName);
    }

    [Fact]
    public async Task The_list_is_read_fresh_every_time_with_no_cache()
    {
        var first = await CreateToken();
        var second = await CreateToken();
        _aspspQueries.Clear();

        await Connect(first);
        await Connect(second);

        Assert.Equal(2, _aspspQueries.Count);
    }

    [Fact]
    public async Task The_limit_is_the_one_in_the_list_at_the_time_the_link_is_used()
    {
        var token = await CreateToken();
        _aspsps = (HttpStatusCode.OK, AspspList("2592000")); // the bank lowered its limit after the link was made

        await Connect(token);

        var validUntil = StartedAuthRequest().GetProperty("access").GetProperty("valid_until").GetString();
        Assert.Equal(T0.AddDays(30).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), validUntil);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "secret-detail")]
    [InlineData(HttpStatusCode.OK, "{\"aspsps\":[]}")]
    public async Task When_the_list_fails_or_no_longer_matches_when_the_link_is_used_nothing_starts(
        HttpStatusCode status, string body)
    {
        var token = await CreateToken();
        _aspsps = (status, body);

        var (page, html) = Page(await Connect(token));

        Assert.Equal(502, page);
        Assert.DoesNotContain("secret-detail", html);
        Assert.Equal(0, BankCalls);                              // no /auth
        Assert.Equal(400, Page(await Connect(token)).Status);    // the link is spent
    }

    [Fact]
    public async Task The_bank_list_never_reaches_the_log()
    {
        _aspsps = (HttpStatusCode.OK, AspspList(name: "Mock ASPSP").Replace("\"DKB\"", "\"list-secret-marker\""));
        var token = await CreateToken();
        await Connect(token);
        _aspsps = (HttpStatusCode.InternalServerError, "list-secret-marker");
        await CreateLink();

        Assert.DoesNotContain(_logs.Lines, l => l.Contains("list-secret-marker"));
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

    // --- Story 2.4: the first sync is requested once, after the consent is stored ------------------

    [Fact]
    public async Task A_completed_consent_requests_the_first_sync_exactly_once()
    {
        var state = await StartedState();

        Assert.Equal(200, Page(await Callback("the-code", state)).Status);
        Assert.Equal(1, _firstSync.Requests);

        // A replay is refused before anything is stored, so it must not request another sync.
        Assert.Equal(400, Page(await Callback("the-code", state)).Status);
        Assert.Equal(1, _firstSync.Requests);
    }

    [Fact]
    public async Task No_first_sync_is_requested_when_the_consent_was_not_completed()
    {
        var refusedState = await StartedState();
        var failingStore = await StartedState();

        // The bank refuses the session request.
        _bank = _ => (HttpStatusCode.Unauthorized, """{"error":"x"}""");
        await Callback("the-code", refusedState);

        // An unknown state, and the bank reporting an error.
        await Callback("c", "never-issued");
        await Callback(null, null, "access_denied");

        // The bank answers, but the consent cannot be stored.
        _bank = path => path == "/auth" ? (HttpStatusCode.OK, $$"""{"url":"{{BankUrl}}"}""") : (HttpStatusCode.OK, SessionBody());
        var broken = new ConsentStore(
            new FakeConsentPersistence { SaveFails = new InvalidOperationException("x") },
            TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await using (var db = NewDb())
        {
            await ConsentEndpoints.CallbackAsync(
                "the-code", failingStore, null, db, NewClient(), broken, _firstSync, _clock, _logs, CancellationToken.None);
        }

        Assert.Equal(0, _firstSync.Requests);
    }

    [Fact]
    public async Task A_failed_first_sync_request_still_shows_Connected_and_logs_only_the_type()
    {
        var state = await StartedState();
        _firstSync.Throws = new InvalidOperationException($"secret detail {BankSessionId}");

        var (status, html) = Page(await Callback("the-code", state));

        Assert.Equal(200, status);
        Assert.Contains("Connected to", html);
        Assert.Equal(BankSessionId, _consent.SessionId);
        Assert.Contains(_logs.Lines, l => l.Contains("InvalidOperationException"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("secret detail") || l.Contains(BankSessionId));
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

        // Cancelled from inside the bank call, so the exception really comes out of the HTTP call (an
        // already-cancelled token would be thrown by the database query before that).
        _beforeBankThrows = cts.Cancel;
        _bankThrows = new OperationCanceledException(cts.Token);

        await using var db = NewDb();
        var client = NewClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConsentEndpoints.ConnectAsync(token, db, client, new AspspResolver(client), _options, _clock, _logs, cts.Token));
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
            ConsentEndpoints.CreateLinkAsync(
                null, db, new AspspResolver(NewClient()), _options, _pending, _clock, _logs, CancellationToken.None));

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
        var broken = new ConsentStore(
            new FakeConsentPersistence { SaveFails = new InvalidOperationException($"secret detail {BankSessionId}") },
            TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await using var db = NewDb();

        var result = await ConsentEndpoints.CallbackAsync(
            "the-code", state, null, db, NewClient(), broken, _firstSync, _clock, _logs, CancellationToken.None);

        var (status, html) = Page(result);
        Assert.Equal(502, status);
        Assert.DoesNotContain("secret detail", html);
        Assert.DoesNotContain(BankSessionId, html);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("secret detail") || l.Contains(BankSessionId));
        Assert.Contains(_logs.Lines, l => l.Contains("InvalidOperationException"));
        Assert.Null(broken.SessionId);
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

    // --- Story 4.3: a reconnect closes the old session ---------------------------------------------

    private async Task<(int Status, string Html)> ConnectBank(string bank, string sessionId)
    {
        _sessionId = sessionId;
        var state = await StartedState(new ConnectLinkRequest(bank, "DE", null));
        return Page(await Callback("the-code", state));
    }

    [Fact]
    public async Task A_reconnect_closes_the_old_session_after_the_new_one_is_stored()
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        Assert.Empty(_deletes); // a first connect has nothing to close

        var (status, _) = await ConnectBank("N26", "new-session-bbbb2222");

        Assert.Equal(200, status);
        var delete = Assert.Single(_deletes);
        Assert.Equal("/sessions/old-session-aaaa1111", delete.Path);
        Assert.Equal("new-session-bbbb2222", delete.StoredSessionAtCall); // stored before closed
        Assert.Equal("new-session-bbbb2222", _consent.SessionIdFor(_consentUser(), "N26"));
    }

    [Fact]
    public async Task The_same_session_id_again_closes_nothing()
    {
        await ConnectBank("N26", "same-session-cccc3333");
        await ConnectBank("N26", "same-session-cccc3333");

        Assert.Empty(_deletes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.Gone, "")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"CLOSED_SESSION\"}")]
    public async Task An_already_closed_or_unknown_old_session_counts_as_fine(HttpStatusCode status, string body)
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        _deleteAnswer = (status, body);

        var (page, _) = await ConnectBank("N26", "new-session-bbbb2222");

        Assert.Equal(200, page);
        Assert.Single(_deletes);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("could not be closed"));
        Assert.Equal("new-session-bbbb2222", _consent.SessionIdFor(_consentUser(), "N26"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.Unauthorized, "Wrong signature")] // no error field: our own fault, not "already closed"
    public async Task A_failing_close_is_logged_without_the_session_id_and_never_breaks_the_reconnect(HttpStatusCode status, string body)
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        _deleteAnswer = (status, body);

        var (page, html) = await ConnectBank("N26", "new-session-bbbb2222");

        Assert.Equal(200, page);
        Assert.Contains("Connected to N26", html);
        Assert.Equal("new-session-bbbb2222", _consent.SessionIdFor(_consentUser(), "N26"));
        Assert.Contains(_logs.Lines, l => l.Contains("could not be closed") && l.Contains("N26"));
        AssertNoSessionIdInLog();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_network_error_or_timeout_on_the_close_never_breaks_the_reconnect(bool timeout)
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        _deleteThrows = timeout
            ? new TaskCanceledException("timed out /sessions/old-session-aaaa1111")
            : new HttpRequestException("down /sessions/old-session-aaaa1111");

        var (page, _) = await ConnectBank("N26", "new-session-bbbb2222");

        Assert.Equal(200, page);
        Assert.Single(_deletes);
        Assert.Equal("new-session-bbbb2222", _consent.SessionIdFor(_consentUser(), "N26"));
        Assert.Contains(_logs.Lines, l => l.Contains("could not be closed"));
        AssertNoSessionIdInLog();
    }

    [Fact]
    public async Task Reconnecting_one_bank_never_touches_another_banks_session()
    {
        await ConnectBank("N26", "n26-session-aaaa1111");
        await ConnectBank("DKB", "dkb-session-bbbb2222");
        Assert.Empty(_deletes);

        await ConnectBank("N26", "n26-session-cccc3333");

        var delete = Assert.Single(_deletes);
        Assert.Equal("/sessions/n26-session-aaaa1111", delete.Path);
        Assert.Equal("dkb-session-bbbb2222", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal("n26-session-cccc3333", _consent.SessionIdFor(_consentUser(), "N26"));
    }

    [Fact]
    public async Task A_failed_store_closes_nothing()
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        var persistence = new FakeConsentPersistence();
        var failing = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await failing.CompleteAsync(_consentUser(), "old-session-aaaa1111", "N26", [], T0, null);
        persistence.SaveFails = new InvalidOperationException("db down");

        _sessionId = "new-session-bbbb2222";
        var state = await StartedState(new ConnectLinkRequest("N26", "DE", null));
        await using var db = NewDb();
        var result = await ConsentEndpoints.CallbackAsync(
            "the-code", state, null, db, NewClient(), failing, _firstSync, _clock, _logs, CancellationToken.None);

        Assert.Equal(502, Page(result).Status);
        Assert.Empty(_deletes);
    }

    [Fact]
    public async Task A_stale_instance_still_closes_the_session_the_database_holds()
    {
        await ConnectBank("N26", "old-session-aaaa1111");
        var persistence = new FakeConsentPersistence();
        var other = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await other.CompleteAsync(_consentUser(), "db-session-dddd4444", "N26", [], T0, null); // another instance stored this
        var stale = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        Assert.Null(stale.SessionIdFor(_consentUser(), "N26")); // this instance's memory is empty

        _sessionId = "new-session-bbbb2222";
        var state = await StartedState(new ConnectLinkRequest("N26", "DE", null));
        await using var db = NewDb();
        var result = await ConsentEndpoints.CallbackAsync(
            "the-code", state, null, db, NewClient(), stale, _firstSync, _clock, _logs, CancellationToken.None);

        Assert.Equal(200, Page(result).Status);
        Assert.Equal("/sessions/db-session-dddd4444", Assert.Single(_deletes).Path);
    }

    // --- several accounts: the current account is chosen by type -------------------------------------

    private static string AccountJson(string uid, string iban, string? type, string product = "Product", string usage = "PRIV") =>
        $$"""{"uid":"{{uid}}","account_id":{"iban":"{{iban}}"},"name":"Name {{uid}}","currency":"EUR","cash_account_type":{{(type is null ? "null" : $"\"{type}\"")}},"product":"{{product}}","usage":"{{usage}}"}""";

    private void OfferAccounts(params string[] accountJson) => _bank = path => path == "/auth"
        ? (HttpStatusCode.OK, $$"""{"url":"{{BankUrl}}"}""")
        : (HttpStatusCode.OK, $$"""{"session_id":"{{_sessionId}}","access":{"valid_until":"2027-01-01T00:00:00Z"},"accounts":[{{string.Join(',', accountJson)}}]}""");

    [Fact]
    public async Task Several_accounts_store_exactly_the_current_account()
    {
        OfferAccounts(
            AccountJson("u1", "DE00000000000000000001", "SVGS"),
            AccountJson("u2", "DE00000000000000000002", "CACC"),
            AccountJson("u3", "DE00000000000000000003", "CARD"));

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        var stored = Assert.Single(_consent.Accounts);
        Assert.Equal("u2", stored.Uid);
        Assert.Equal("CACC", stored.CashAccountType);
        Assert.Equal("Product", stored.Product);
        Assert.Equal("PRIV", stored.Usage);
        Assert.Equal(1, _firstSync.Requests);
        Assert.Empty(_deletes);
    }

    [Theory]
    [InlineData("SVGS", "CARD")]   // no CACC at all
    [InlineData("CACC", "cacc")]   // two current accounts
    [InlineData(null, null)]       // types missing
    public async Task Several_accounts_without_exactly_one_current_account_store_nothing_and_close_the_new_session(string? first, string? second)
    {
        OfferAccounts(
            AccountJson("u1", "DE00000000000000000001", first),
            AccountJson("u2", "DE00000000000000000002", second));

        var (status, html) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(400, status);
        Assert.Contains("none could be chosen safely", html);
        Assert.Null(_consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Empty(_consent.Accounts);
        Assert.Equal(0, _firstSync.Requests);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        // The log shows what the bank sent per account, never who owns it.
        Assert.Contains(_logs.Lines, l => l.Contains("offered 2 account(s)") && l.Contains("/PRIV/EUR"));
        Assert.Contains(_logs.Lines, l => l.Contains("refused accounts") && l.Contains("/Product/PRIV/EUR"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("DE0000000000000000000") || l.Contains("Name u1") || l.Contains("dkb-session-eeee5555"));
    }

    [Fact]
    public async Task A_successful_connect_never_logs_the_product_text()
    {
        OfferAccounts(
            AccountJson("u1", "DE00000000000000000001", "SVGS", product: "Max Mustermann DE99FAKEIBAN"),
            AccountJson("u2", "DE00000000000000000002", "CACC", product: "Max Mustermann DE99FAKEIBAN"));

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        Assert.Contains(_logs.Lines, l => l.Contains("offered 2 account(s)") && l.Contains("CACC/PRIV/EUR"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("Mustermann") || l.Contains("FAKEIBAN"));
    }

    [Fact]
    public async Task A_refused_reconnect_leaves_the_old_consent_and_session_untouched_and_closes_only_the_new_session()
    {
        await ConnectBank("DKB", "old-session-aaaa1111");
        var before = Assert.Single(_consent.Accounts);

        OfferAccounts(
            AccountJson("u1", "DE00000000000000000001", "SVGS"),
            AccountJson("u2", "DE00000000000000000002", "CARD"));
        var (status, _) = await ConnectBank("DKB", "new-session-bbbb2222");

        Assert.Equal(400, status);
        Assert.Equal("old-session-aaaa1111", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal(before, Assert.Single(_consent.Accounts));
        Assert.Equal("/sessions/new-session-bbbb2222", Assert.Single(_deletes).Path);
    }

    [Fact]
    public async Task An_empty_account_list_is_stored_as_before()
    {
        OfferAccounts();

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        Assert.Equal("dkb-session-eeee5555", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Empty(_consent.Accounts);
        Assert.Empty(_deletes);
    }

    [Theory]
    [InlineData("SVGS")]
    [InlineData(null)]
    public async Task Exactly_one_account_is_stored_whatever_its_type(string? type)
    {
        OfferAccounts(AccountJson("u1", "DE00000000000000000001", type));

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        Assert.Equal("u1", Assert.Single(_consent.Accounts).Uid);
        Assert.Empty(_deletes);
    }

    private Guid _consentUser() => _consent.UserId;

    private void AssertNoSessionIdInLog()
    {
        foreach (var id in new[] { "old-session-aaaa1111", "new-session-bbbb2222" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(id));
        }
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
