using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreedyNose.Api.Auth;
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
    private readonly ConsentCipher _cipher = new(RandomNumberGenerator.GetBytes(32));
    private Guid _attemptId; // the attempt of the link created last by CreateToken

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

    /// <summary>Fails exactly one save, the n-th of this context (1-based), like a database that blinks once.</summary>
    private sealed class FailNthSaveInterceptor(int failOn) : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ++_saves == failOn ? throw new InvalidOperationException("db blip") : ValueTask.FromResult(result);
    }

    /// <summary>Runs an action after the first save of this context went through (a client hanging up right after the claim).</summary>
    private sealed class AfterFirstSaveInterceptor(Action action) : SaveChangesInterceptor
    {
        private bool _done;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                action();
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>A consent persistence that honours the token like the real one and can be told to throw on the old-session read.</summary>
    private sealed class ProbePersistence(FakeConsentPersistence inner) : IConsentPersistence
    {
        public Exception? LoadSessionThrows { get; set; }

        public Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default) => inner.SaveAsync(snapshot, ct);

        public Task<string?> LoadSessionIdAsync(Guid userId, string aspspName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return LoadSessionThrows is null ? inner.LoadSessionIdAsync(userId, aspspName, ct) : Task.FromException<string?>(LoadSessionThrows);
        }

        public Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
    }

    private GreedyNoseDbContext NewDb(bool lostRace = false, int? failSave = null, Action? afterFirstSave = null)
    {
        var builder = new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(_dbName);
        if (lostRace)
        {
            builder.AddInterceptors(new LostRaceInterceptor());
        }

        if (failSave is { } failOn)
        {
            builder.AddInterceptors(new FailNthSaveInterceptor(failOn));
        }

        if (afterFirstSave is not null)
        {
            builder.AddInterceptors(new AfterFirstSaveInterceptor(afterFirstSave));
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
            request, db, new AspspResolver(NewClient()), NewClient(), _cipher, _consent, _options, _pending, _clock, _logs, CancellationToken.None);
    }

    private async Task<string> CreateToken(ConnectLinkRequest? request = null)
    {
        var ok = Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink(request));
        _attemptId = ok.Value!.AttemptId;
        var query = new Uri(ok.Value.Url).Query;
        return Uri.UnescapeDataString(query["?token=".Length..]);
    }

    private async Task<IResult> Connect(string? token, bool lostRace = false)
    {
        await using var db = NewDb(lostRace);
        var client = NewClient();
        return await ConsentEndpoints.ConnectAsync(
            token, db, client, new AspspResolver(client), _options, _clock, _logs, CancellationToken.None);
    }

    private async Task<IResult> Callback(string? code, string? state, string? error = null, bool lostRace = false, int? failSave = null)
    {
        await using var db = NewDb(lostRace, failSave);
        return await ConsentEndpoints.CallbackAsync(
            code, state, error, db, NewClient(), _cipher, _consent, _firstSync, _clock, _logs, CancellationToken.None);
    }

    /// <summary>Link created and used; returns the state the bank was given.</summary>
    private async Task<string> StartedState(ConnectLinkRequest? request = null)
    {
        await ExpireStoredConsentsAsync();
        var token = await CreateToken(request);
        Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));
        return ReadState(_calls.Last(c => c.Path == "/auth").Body);
    }

    /// <summary>
    /// gn-4b3: a bank with a live consent cannot be started again, so every test that reconnects first lets the
    /// stored consent run out (same session, same account, only the end date moves into the past).
    /// </summary>
    private async Task ExpireStoredConsentsAsync()
    {
        foreach (var state in _consent.GetStates(T0).Where(s => s.Status == ConsentStatus.Active))
        {
            await _consent.CompleteAsync(state.UserId, state.SessionId!, state.AspspName!, [state.Account!],
                state.ConnectedAt ?? T0.AddDays(-10), T0.AddDays(-1));
        }
    }

    private static string ReadState(string authBody) =>
        JsonDocument.Parse(authBody).RootElement.GetProperty("state").GetString()!;

    /// <summary>The one neutral callback page (side-effect free path); every callback branch must equal it.</summary>
    private async Task<string> NeutralHtml() => Page(await Callback(null, null, "x")).Html;

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
        Assert.Contains("You can close this page.", html);
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
        Assert.Contains("You can close this page.", html);
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
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("You can close this page.", html);
    }

    [Fact]
    public async Task A_bank_error_text_never_reaches_a_log_line_only_its_reason_does()
    {
        await Callback(null, null, "a\r\nb SECRET-BANK-TEXT" + new string('x', 5000));

        // The text is chosen by whoever builds the query string: only the reason it maps to is logged.
        var line = Assert.Single(_logs.Lines, l => l.Contains("returned an error"));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("bank-error", line);
        Assert.True(line.Length < 100);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("SECRET-BANK-TEXT") || l.Contains("xxxx"));
    }

    [Fact]
    public async Task A_bank_name_is_html_encoded_on_the_success_page()
    {
        var state = await StartedState(new ConnectLinkRequest("<b>Evil</b> & Co", "DE", "personal"));

        var (_, html) = Page(await Callback("the-code", state));

        Assert.DoesNotContain("Evil", html);
        Assert.DoesNotContain("N26", html);
        Assert.Contains("You can close this page.", html);
    }

    // --- Story 5.1a: one neutral page for every callback outcome -------------------------------------

    [Fact]
    public async Task Success_failure_and_a_spent_state_end_in_the_same_neutral_page()
    {
        var state = await StartedState();
        var (okStatus, ok) = Page(await Callback("the-code", state));
        var (spentStatus, spent) = Page(await Callback("the-code", state));
        var (errStatus, err) = Page(await Callback(null, null, "access_denied"));
        var (unknownStatus, unknown) = Page(await Callback("c", "no-such-state"));

        Assert.Equal(200, okStatus);
        Assert.Equal(400, spentStatus);
        Assert.Equal(400, errStatus);
        Assert.Equal(400, unknownStatus);
        Assert.Equal(ok, spent);
        Assert.Equal(ok, err);
        Assert.Equal(ok, unknown);
        Assert.Contains("You can close this page.", ok);
        Assert.Contains("Greedy Nose shows the result.", ok);
    }

    [Fact]
    public async Task A_failed_session_trade_keeps_its_status_but_shows_the_neutral_page()
    {
        var (okStatus, ok) = Page(await Callback("the-code", await StartedState()));
        Assert.Equal(200, okStatus);
        var state = await StartedState();
        _bank = _ => (HttpStatusCode.Unauthorized, """{"error":"x"}""");

        var (status, html) = Page(await Callback("bad-code", state));

        Assert.Equal(502, status);
        Assert.Equal(ok, html);
    }

    [Fact]
    public async Task The_neutral_page_has_a_visible_link_and_a_script_to_the_same_secretless_link()
    {
        var state = await StartedState(new ConnectLinkRequest("N26", "DE", "personal"));

        var (_, html) = Page(await Callback("the-code", state));

        Assert.Contains("href=\"greedynose://connected\"", html);
        Assert.Contains(">Open Greedy Nose</a>", html);
        Assert.Contains("<script>", html);
        Assert.Contains("window.location.href=\"greedynose://connected\"", html);
        Assert.DoesNotContain(state, html);
        Assert.DoesNotContain("the-code", html);
        Assert.DoesNotContain("N26", html);
        Assert.DoesNotContain("Connected to", html);
        Assert.DoesNotContain("failed", html, StringComparison.OrdinalIgnoreCase);
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
                "the-code", failingStore, null, db, NewClient(), _cipher, broken, _firstSync, _clock, _logs, CancellationToken.None);
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
        Assert.Contains("You can close this page.", html);
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
        Assert.Equal(await NeutralHtml(), html);
        Assert.DoesNotContain("secret-detail", html);
        Assert.Null(_consent.SessionId);
    }

    [Fact]
    public async Task A_session_response_without_a_session_id_stores_nothing()
    {
        var state = await StartedState();
        _bank = _ => (HttpStatusCode.OK, """{"accounts":[]}""");

        var (idStatus, idHtml) = Page(await Callback("the-code", state));
        Assert.Equal(502, idStatus);
        Assert.Equal(await NeutralHtml(), idHtml);
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
    public async Task Spending_the_link_the_state_and_recording_the_outcome_each_bump_Version()
    {
        var token = await CreateToken();
        var created = (await RowAsync(_attemptId)).Version;
        Assert.IsType<RedirectHttpResult>(Unwrap(await Connect(token)));
        var state = ReadState(_calls.Last(c => c.Path == "/auth").Body);
        var linkSpent = (await RowAsync(_attemptId)).Version;
        Assert.True(linkSpent > created);

        await Callback("the-code", state);

        var outcomeRecorded = (await RowAsync(_attemptId)).Version;
        Assert.True(outcomeRecorded > linkSpent);
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
        Assert.Equal(await NeutralHtml(), html);
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
                null, db, new AspspResolver(NewClient()), NewClient(), _cipher, _consent, _options, _pending, _clock, _logs, CancellationToken.None));

        Assert.IsType<DbUpdateConcurrencyException>(ex);
        Assert.Equal(1, db.ChangeTracker.Entries<PendingConsent>().Count(e => e.State == EntityState.Added));
    }

    private Task StoreConsentAsync(string aspsp, DateTimeOffset expiresAt) =>
        _consent.CompleteAsync(SeedData.UserId, "stored-session", aspsp,
            [new ConnectedAccount("uid", BankIban, "Main", "EUR", null)], T0.AddDays(-10), expiresAt);

    [Theory]
    [InlineData("N26")]
    [InlineData("n26")]
    public async Task A_connected_bank_gets_409_and_no_link_is_stored(string requested)
    {
        await StoreConsentAsync("N26", T0.AddDays(40));

        var result = await CreateLink(new ConnectLinkRequest(requested, "DE", null));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(409, problem.StatusCode);
        await using var db = NewDb();
        Assert.Empty(db.PendingConsents);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(BankIban));
    }

    [Fact]
    public async Task An_expired_bank_may_be_reconnected()
    {
        await StoreConsentAsync("N26", T0.AddDays(-1));

        Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink(new ConnectLinkRequest("N26", "DE", null)));
    }

    [Fact]
    public async Task Another_bank_may_be_started_while_one_is_connected()
    {
        await StoreConsentAsync("N26", T0.AddDays(40));

        Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink(new ConnectLinkRequest("DKB", "DE", null)));
    }

    [Fact]
    public async Task A_bank_that_is_not_in_the_list_is_refused_before_the_connected_check()
    {
        await StoreConsentAsync("Unlisted", T0.AddDays(40));

        var problem = Assert.IsType<ProblemHttpResult>(await CreateLink(new ConnectLinkRequest("Unlisted", "DE", null)));
        Assert.Equal(400, problem.StatusCode);
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
            "the-code", state, null, db, NewClient(), _cipher, broken, _firstSync, _clock, _logs, CancellationToken.None);

        var (status, html) = Page(result);
        Assert.Equal(502, status);
        Assert.Equal(await NeutralHtml(), html);
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
        Assert.Contains("You can close this page.", html);
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
    public async Task A_failed_store_keeps_the_old_session_and_closes_the_new_one()
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
            "the-code", state, null, db, NewClient(), _cipher, failing, _firstSync, _clock, _logs, CancellationToken.None);

        var (storeStatus, storeHtml) = Page(result);
        Assert.Equal(502, storeStatus);
        Assert.Equal(await NeutralHtml(), storeHtml);
        Assert.Equal("/sessions/new-session-bbbb2222", Assert.Single(_deletes).Path); // the traded session is useless
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
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
            "the-code", state, null, db, NewClient(), _cipher, stale, _firstSync, _clock, _logs, CancellationToken.None);

        Assert.Equal(200, Page(result).Status);
        Assert.Equal("/sessions/db-session-dddd4444", Assert.Single(_deletes).Path);
    }

    // --- Story 5.3: accounts in euro, and the attempt the app follows ------------------------------

    private static string AccountJson(string uid, string iban, string? type, string product = "Product", string usage = "PRIV", string? currency = "EUR") =>
        $$"""{"uid":"{{uid}}","account_id":{"iban":"{{iban}}"},"name":"Name {{uid}}","currency":{{(currency is null ? "null" : $"\"{currency}\"")}},"cash_account_type":{{(type is null ? "null" : $"\"{type}\"")}},"product":"{{product}}","usage":"{{usage}}"}""";

    private void OfferAccounts(params string[] accountJson) => _bank = path => path == "/auth"
        ? (HttpStatusCode.OK, $$"""{"url":"{{BankUrl}}"}""")
        : (HttpStatusCode.OK, $$"""{"session_id":"{{_sessionId}}","access":{"valid_until":"2027-01-01T00:00:00Z"},"accounts":[{{string.Join(',', accountJson)}}]}""");

    private sealed class TestUser(Guid id) : ICurrentUser
    {
        public Guid UserId => id;
    }

    /// <summary>Asks for the state of an attempt the way the Function does; returns the status and the JSON text.</summary>
    private async Task<(int Status, JsonElement Body, string Text)> AttemptState(string id, Guid? user = null, ConsentCipher? cipher = null)
    {
        await using var db = NewDb();
        var result = await ConnectAttempts.HandleAsync(
            id, new TestUser(user ?? SeedData.UserId), db, NewClient(), cipher ?? _cipher, _clock, _logs, CancellationToken.None);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var text = result is IValueHttpResult { Value: not null } v ? JsonSerializer.Serialize(v.Value, JsonSerializerOptions.Web) : "{}";
        return (status, JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    private async Task<string> StateOf(Guid attemptId)
    {
        var (status, body, _) = await AttemptState(attemptId.ToString());
        Assert.Equal(200, status);
        return body.GetProperty("state").GetString()!;
    }

    private async Task<string?> ReasonOf(Guid attemptId)
    {
        var (_, body, _) = await AttemptState(attemptId.ToString());
        return body.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    private async Task<PendingConsent> RowAsync(Guid attemptId)
    {
        await using var db = NewDb();
        return await db.PendingConsents.AsNoTracking().SingleAsync(p => p.Id == attemptId);
    }

    private const string Iban1 = "DE00000000000000000001";
    private const string Iban2 = "DE00000000000000000002";

    [Fact]
    public async Task A_link_answers_the_url_and_the_id_of_the_attempt_which_is_the_stored_row()
    {
        var ok = Assert.IsType<Ok<ConnectLinkResponse>>(await CreateLink());

        Assert.NotEqual(Guid.Empty, ok.Value!.AttemptId);
        Assert.Equal(ok.Value.AttemptId, (await RowAsync(ok.Value.AttemptId)).Id);
        Assert.DoesNotContain(ok.Value.AttemptId.ToString(), ok.Value.Url);
        Assert.Equal("waiting", await StateOf(ok.Value.AttemptId));
    }

    [Fact]
    public async Task One_euro_account_is_saved_the_first_sync_queued_and_the_state_is_saved()
    {
        var state = await StartedState();

        var (status, _) = Page(await Callback("the-code", state));

        Assert.Equal(200, status);
        Assert.Equal(BankIban, Assert.Single(_consent.Accounts).Iban);
        Assert.Equal(1, _firstSync.Requests);
        Assert.Equal("saved", await StateOf(_attemptId));
        var row = await RowAsync(_attemptId);
        Assert.Null(row.WaitingPayload);
        Assert.Null(row.FailureReason);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("eur")]
    [InlineData("Eur")]
    public async Task The_currency_is_compared_ignoring_case(string currency)
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC", currency: currency));
        var state = await StartedState();

        Assert.Equal(200, Page(await Callback("the-code", state)).Status);
        Assert.Single(_consent.Accounts);
    }

    [Fact]
    public async Task Of_several_accounts_only_the_euro_ones_count_and_one_of_them_is_saved()
    {
        OfferAccounts(
            AccountJson("u1", Iban1, "CACC", currency: "USD"),
            AccountJson("u2", Iban2, "CACC"),
            AccountJson("u3", "DE00000000000000000003", "CACC", currency: null));
        var state = await StartedState();

        Assert.Equal(200, Page(await Callback("the-code", state)).Status);
        Assert.Equal("u2", Assert.Single(_consent.Accounts).Uid);
        Assert.Equal("saved", await StateOf(_attemptId));
    }

    [Fact]
    public async Task Several_euro_accounts_save_nothing_and_wait_for_the_choice_with_a_deadline()
    {
        await ConnectBank("DKB", "old-session-aaaa1111"); // a bank with an older session: a reconnect
        _clock.Now = T0.AddMinutes(1);
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"), AccountJson("u3", "DE00000000000000000003", "SVGS", currency: "USD"));

        var (status, html) = await ConnectBank("DKB", "new-session-bbbb2222");

        Assert.Equal(200, status);
        Assert.Equal(await NeutralHtml(), html);
        Assert.Equal("choose_account", await StateOf(_attemptId));
        // Nothing saved as a connection: the old consent, its session and its account are untouched, and nothing is closed yet.
        Assert.Equal("old-session-aaaa1111", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal(BankIban, Assert.Single(_consent.Accounts).Iban);
        Assert.Equal(1, _firstSync.Requests); // only the first connect's
        Assert.Empty(_deletes);
        var row = await RowAsync(_attemptId);
        Assert.Equal(T0.AddMinutes(16), row.ChoiceDeadline);
        Assert.Equal(ConnectOutcome.ChooseAccount, row.Outcome);
    }

    [Fact]
    public async Task The_waiting_payload_is_ciphertext_bound_to_its_row_and_column()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");

        var row = await RowAsync(_attemptId);
        var raw = Encoding.UTF8.GetString(row.WaitingPayload!);
        Assert.DoesNotContain("dkb-session-eeee5555", raw);
        Assert.DoesNotContain(Iban1, raw);
        Assert.DoesNotContain("Name u1", raw);

        // It opens with the key, and only for its own row.
        var choice = WaitingPayload.Open(_cipher, row.Id, row.WaitingPayload!);
        Assert.Equal("dkb-session-eeee5555", choice.SessionId);
        Assert.Equal([Iban1, Iban2], choice.Accounts.Select(a => a.Iban));
        Assert.Throws<ConsentDecryptionException>(() => WaitingPayload.Open(_cipher, Guid.NewGuid(), row.WaitingPayload!));
        Assert.Throws<ConsentDecryptionException>(() =>
            WaitingPayload.Open(new ConsentCipher(RandomNumberGenerator.GetBytes(32)), row.Id, row.WaitingPayload!));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("foreign")]
    [InlineData("missing")]
    public async Task No_euro_account_closes_the_new_session_stores_nothing_and_fails_with_a_reason(string kind)
    {
        switch (kind)
        {
            case "none": OfferAccounts(); break;
            case "foreign": OfferAccounts(AccountJson("u1", Iban1, "CACC", currency: "USD"), AccountJson("u2", Iban2, "CACC", currency: "GBP")); break;
            default: OfferAccounts(AccountJson("u1", Iban1, "CACC", currency: null)); break;
        }

        var (status, html) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(400, status);
        Assert.Equal(await NeutralHtml(), html);
        Assert.Empty(_consent.Accounts);
        Assert.Null(_consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal(0, _firstSync.Requests);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("no-supported-account", await ReasonOf(_attemptId));
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        AssertNoAccountDataInLog();
    }

    [Fact]
    public async Task A_refused_reconnect_leaves_the_old_consent_and_session_untouched_and_closes_only_the_new_session()
    {
        await ConnectBank("DKB", "old-session-aaaa1111");
        var before = Assert.Single(_consent.Accounts);

        OfferAccounts(AccountJson("u1", Iban1, "SVGS", currency: "USD"));
        var (status, _) = await ConnectBank("DKB", "new-session-bbbb2222");

        Assert.Equal(400, status);
        Assert.Equal("old-session-aaaa1111", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal(before, Assert.Single(_consent.Accounts));
        Assert.Equal("/sessions/new-session-bbbb2222", Assert.Single(_deletes).Path);
    }

    [Theory]
    [InlineData("SVGS")]
    [InlineData(null)]
    public async Task Exactly_one_euro_account_is_saved_whatever_its_type(string? type)
    {
        OfferAccounts(AccountJson("u1", Iban1, type));

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        Assert.Equal("u1", Assert.Single(_consent.Accounts).Uid);
        Assert.Empty(_deletes);
    }

    [Fact]
    public async Task A_successful_connect_never_logs_the_product_text_or_account_data()
    {
        OfferAccounts(
            AccountJson("u1", Iban1, "CACC", product: "Max Mustermann DE99FAKEIBAN"),
            AccountJson("u2", Iban2, "CACC", product: "Girokonto"));

        var (status, _) = await ConnectBank("DKB", "dkb-session-eeee5555");

        Assert.Equal(200, status);
        Assert.Contains(_logs.Lines, l => l.Contains("offered 2 account(s)") && l.Contains("CACC/PRIV/EUR") && l.Contains("in euro: 2"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("Mustermann") || l.Contains("FAKEIBAN") || l.Contains("Girokonto"));
        AssertNoAccountDataInLog();
    }

    private void AssertNoAccountDataInLog()
    {
        Assert.DoesNotContain(_logs.Lines, l =>
            l.Contains("dkb-session-eeee5555") || l.Contains(Iban1) || l.Contains(Iban2) || l.Contains("Name u1"));
    }

    [Fact]
    public async Task A_bank_error_fails_the_attempt_by_its_state_with_a_code_and_no_bank_text()
    {
        var state = await StartedState();

        var (status, html) = Page(await Callback(null, state, "server_error SECRET-BANK-TEXT"));

        Assert.Equal(400, status);
        Assert.Equal(await NeutralHtml(), html);
        var (_, _, text) = await AttemptState(_attemptId.ToString());
        Assert.Equal("""{"state":"failed","reason":"bank-error"}""", text);
        Assert.DoesNotContain(_calls, c => c.Path == "/sessions");
        // The state is spent: the same callback with a code now trades nothing.
        Assert.Equal(400, Page(await Callback("the-code", state)).Status);
        Assert.DoesNotContain(_calls, c => c.Path == "/sessions");
    }

    [Fact]
    public async Task A_cancel_fails_the_attempt_with_the_reason_cancelled()
    {
        var state = await StartedState();

        Assert.Equal(400, Page(await Callback(null, state, "access_denied")).Status);

        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("cancelled", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task An_error_without_a_usable_state_marks_nothing()
    {
        await StartedState();

        Assert.Equal(400, Page(await Callback(null, null, "access_denied")).Status);
        Assert.Equal(400, Page(await Callback(null, "unknown-state", "access_denied")).Status);

        Assert.Equal("waiting", await StateOf(_attemptId));
        Assert.Equal(400, Page(await Callback(null, new string('x', 500), "access_denied")).Status);
        Assert.Equal("waiting", await StateOf(_attemptId));
    }

    [Fact]
    public async Task A_trade_that_fails_gives_code_not_traded()
    {
        var state = await StartedState();
        _bank = path => path == "/auth" ? (HttpStatusCode.OK, "{}") : (HttpStatusCode.BadRequest, "SECRET-BANK-TEXT");

        Assert.Equal(502, Page(await Callback("the-code", state)).Status);

        Assert.Equal("code-not-traded", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task A_network_fault_on_the_trade_gives_code_not_traded()
    {
        var state = await StartedState();
        _bankThrows = new HttpRequestException("down");

        Assert.Equal(502, Page(await Callback("the-code", state)).Status);

        Assert.Equal("code-not-traded", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task An_answer_without_a_session_id_gives_bank_answer_unusable()
    {
        var state = await StartedState();
        _bank = path => path == "/auth" ? (HttpStatusCode.OK, "{}") : (HttpStatusCode.OK, """{"accounts":[]}""");

        Assert.Equal(502, Page(await Callback("the-code", state)).Status);

        Assert.Equal("bank-answer-unusable", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task A_consent_that_cannot_be_stored_gives_store_failed()
    {
        var state = await StartedState();
        var persistence = new FakeConsentPersistence { SaveFails = new InvalidOperationException("db down") };
        var failing = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await using var db = NewDb();

        var result = await ConsentEndpoints.CallbackAsync(
            "the-code", state, null, db, NewClient(), _cipher, failing, _firstSync, _clock, _logs, CancellationToken.None);

        Assert.Equal(502, Page(result).Status);
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
    }

    // --- GET /connect-attempts/{id} ---------------------------------------------------------------

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_malformed_or_unknown_id_is_404(string id)
    {
        await CreateLink();

        Assert.Equal(404, (await AttemptState(id)).Status);
    }

    [Fact]
    public async Task Another_users_attempt_is_404()
    {
        await CreateLink();

        Assert.Equal(404, (await AttemptState(_attemptId.ToString(), user: Guid.NewGuid())).Status);
    }

    [Fact]
    public async Task A_started_attempt_waits_and_expires_with_its_deadline()
    {
        await StartedState();
        Assert.Equal("waiting", await StateOf(_attemptId));

        _clock.Now = T0 + _pending.Lifetime;

        Assert.Equal("expired", await StateOf(_attemptId));
    }

    [Fact]
    public async Task The_answers_have_exactly_the_documented_shape()
    {
        await CreateToken();
        Assert.Equal("""{"state":"waiting"}""", (await AttemptState(_attemptId.ToString())).Text);

        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        Assert.Equal("""{"state":"choose_account"}""", (await AttemptState(_attemptId.ToString())).Text);
    }

    [Fact]
    public async Task A_choice_whose_deadline_passed_is_expired_its_session_closed_and_its_payload_deleted()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _clock.Now = T0.AddMinutes(14);
        Assert.Equal("choose_account", await StateOf(_attemptId)); // a read before the deadline closes nothing
        Assert.Empty(_deletes);

        _clock.Now = T0.AddMinutes(16);

        Assert.Equal("expired", await StateOf(_attemptId));
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        var row = await RowAsync(_attemptId);
        Assert.Null(row.WaitingPayload);
        Assert.Null(_consent.SessionIdFor(_consentUser(), "DKB"));

        Assert.Equal("expired", await StateOf(_attemptId)); // closed once, not again
        Assert.Single(_deletes);
        AssertNoAccountDataInLog();
    }

    [Fact]
    public async Task A_failing_close_keeps_the_payload_and_the_row_even_past_the_flows_own_deadline()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteThrows = new HttpRequestException("down /sessions/dkb-session-eeee5555");
        _clock.Now = T0.AddHours(2); // past the deadline of the choice and past ExpiresAt of the row

        Assert.Equal("expired", await StateOf(_attemptId));
        await CreateLink(new ConnectLinkRequest("ING", "DE", null)); // the cleanup of old rows must not drop it either

        var row = await RowAsync(_attemptId);
        Assert.NotNull(row.WaitingPayload);
        Assert.Equal("dkb-session-eeee5555", WaitingPayload.Open(_cipher, row.Id, row.WaitingPayload!).SessionId);
        Assert.Contains(_logs.Lines, l => l.Contains("could not be closed"));
        AssertNoAccountDataInLog();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)] // no error field: our own fault, not "already closed"
    public async Task A_refusing_bank_counts_as_a_failed_close(HttpStatusCode status)
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteAnswer = (status, "Wrong signature");
        _clock.Now = T0.AddMinutes(20);

        await StateOf(_attemptId);

        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task An_already_gone_session_counts_as_closed(HttpStatusCode status)
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteAnswer = (status, "");
        _clock.Now = T0.AddMinutes(20);

        await StateOf(_attemptId);

        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
    }

    [Fact]
    public async Task The_next_read_or_tick_retries_a_failed_close_and_then_deletes_the_payload()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteThrows = new HttpRequestException("down");
        _clock.Now = T0.AddMinutes(20);
        await StateOf(_attemptId);
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);

        _clock.Now = T0.AddHours(3);
        await StateOf(_attemptId); // still down: tried again, kept again
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Equal(2, _deletes.Count);

        _deleteThrows = null; // the bank is back; this time it is the tick that retries (6 h later, past the backoff)
        _clock.Now = T0.AddHours(9);
        var cleanup = new ConnectAttemptCleanup(
            new TestDbFactory(this), NewClient, _cipher, _clock, NullLogger<ConnectAttemptCleanup>.Instance);
        await cleanup.RunAsync(CancellationToken.None);

        Assert.Equal(3, _deletes.Count);
        Assert.All(_deletes, d => Assert.Equal("/sessions/dkb-session-eeee5555", d.Path));
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        await StateOf(_attemptId);
        Assert.Equal(3, _deletes.Count); // closed once, not again
    }

    [Fact]
    public async Task A_failed_close_is_not_tried_again_within_the_backoff_but_is_after_it()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteThrows = new HttpRequestException("down");
        _clock.Now = T0.AddMinutes(20);
        await StateOf(_attemptId);
        Assert.Single(_deletes);

        _clock.Now = T0.AddMinutes(20) + ConnectAttempts.CloseBackoff - TimeSpan.FromSeconds(1);
        await StateOf(_attemptId);
        await CreateLink(new ConnectLinkRequest("ING", "DE", null)); // a new link asks the same question
        Assert.Single(_deletes); // no second call to the bank within the pause

        _clock.Now = T0.AddMinutes(20) + ConnectAttempts.CloseBackoff;
        await StateOf(_attemptId);
        Assert.Equal(2, _deletes.Count);
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);

        _deleteThrows = null;
        _clock.Now = T0.AddMinutes(40);
        await StateOf(_attemptId);
        Assert.Equal(3, _deletes.Count);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
    }

    [Fact]
    public async Task A_failed_row_that_still_holds_a_payload_gets_its_session_closed_and_is_then_removable()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        // As if the choice was saved but the call failed, and the attempt was marked failed without clearing it.
        await using (var db = NewDb())
        {
            var row = await db.PendingConsents.SingleAsync(p => p.Id == _attemptId);
            row.Outcome = ConnectOutcome.Failed;
            row.FailureReason = ConnectFailure.StoreFailed;
            await db.SaveChangesAsync();
        }

        Assert.Equal("failed", await StateOf(_attemptId)); // well before the choice deadline: it is not live any more

        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);

        _clock.Now = T0.AddHours(2);
        await CreateLink(new ConnectLinkRequest("ING", "DE", null));
        await using var after = NewDb();
        Assert.DoesNotContain(after.PendingConsents, p => p.Id == _attemptId);
    }

    [Fact]
    public async Task A_live_choice_is_left_alone_by_the_cleanup()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _clock.Now = T0.AddMinutes(5);

        await StateOf(_attemptId);
        await CreateLink(new ConnectLinkRequest("ING", "DE", null));

        Assert.Empty(_deletes);
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_decrypted_is_deleted_at_once_without_a_bank_call()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _clock.Now = T0.AddMinutes(20);

        var (status, _, _) = await AttemptState(_attemptId.ToString(), cipher: new ConsentCipher(RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(200, status);
        Assert.Empty(_deletes);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Contains(_logs.Lines, l => l.Contains("could not be read") && l.Contains("ConsentDecryptionException"));
        AssertNoAccountDataInLog();
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_close_as_not_closed_and_keeps_the_payload()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.False(await ConnectAttempts.CloseSessionAsync(NewClient(), "dkb-session-eeee5555", "DKB", _logs.CreateLogger("t"), "The waiting session", cancelled.Token));

        _clock.Now = T0.AddMinutes(20);
        var cleanup = new ConnectAttemptCleanup(
            new TestDbFactory(this), NewClient, _cipher, _clock, NullLogger<ConnectAttemptCleanup>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.RunAsync(cancelled.Token));
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
        AssertNoAccountDataInLog();
    }

    [Fact]
    public async Task A_cancel_callback_whose_first_write_fails_still_spends_the_state_and_cannot_be_replayed()
    {
        var state = await StartedState();

        // The only save of the cancel branch is the mark (fails); the retry re-applies all of it, spent state included.
        Assert.Equal(400, Page(await Callback(null, state, "access_denied", failSave: 1)).Status);

        var row = await RowAsync(_attemptId);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal("cancelled", row.FailureReason);
        Assert.Equal(400, Page(await Callback("the-code", state)).Status);
        Assert.DoesNotContain(_calls, c => c.Path == "/sessions");
        Assert.Equal("cancelled", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task A_close_that_keeps_failing_is_given_up_a_day_after_the_deadline_with_one_warning()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        _deleteThrows = new HttpRequestException("down");
        var deadline = (await RowAsync(_attemptId)).ChoiceDeadline!.Value;

        _clock.Now = deadline + ConnectAttempts.CloseRetryWindow - TimeSpan.FromMinutes(1);
        await StateOf(_attemptId);
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);

        _clock.Now = deadline + ConnectAttempts.CloseRetryWindow;
        await StateOf(_attemptId);

        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        var warning = Assert.Single(_logs.Lines, l => l.Contains("giving up"));
        Assert.Contains("DKB", warning);
        AssertNoAccountDataInLog();
    }

    [Fact]
    public async Task A_choice_that_cannot_be_kept_closes_the_new_session_and_fails_with_store_failed()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        _sessionId = "dkb-session-eeee5555";
        var state = await StartedState(new ConnectLinkRequest("DKB", "DE", null));

        // Saves of this callback: 1 spends the state, 2 keeps the choice (fails), 3 records the failure.
        var (status, html) = Page(await Callback("the-code", state, failSave: 2));

        Assert.Equal(502, status);
        Assert.Equal(await NeutralHtml(), html);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Empty(_consent.Accounts);
    }

    [Fact]
    public async Task A_replayed_callback_after_the_choice_trades_no_second_code_and_changes_nothing()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        _sessionId = "dkb-session-eeee5555";
        var state = await StartedState(new ConnectLinkRequest("DKB", "DE", null));
        Assert.Equal(200, Page(await Callback("the-code", state)).Status);
        var before = await RowAsync(_attemptId);
        var sessionCalls = _calls.Count(c => c.Path == "/sessions");

        Assert.Equal(400, Page(await Callback("the-code", state)).Status);
        Assert.Equal(400, Page(await Callback(null, state, "access_denied")).Status);

        Assert.Equal(sessionCalls, _calls.Count(c => c.Path == "/sessions"));
        Assert.Equal("choose_account", await StateOf(_attemptId));
        var after = await RowAsync(_attemptId);
        Assert.Equal(before.WaitingPayload, after.WaitingPayload);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task One_failing_write_of_the_saved_outcome_is_retried_once_and_the_page_is_unchanged()
    {
        var state = await StartedState();

        // Saves of this callback: 1 spends the state, 2 records "saved" (fails), 3 is the retry.
        var (status, html) = Page(await Callback("the-code", state, failSave: 2));

        Assert.Equal(200, status);
        Assert.Equal(await NeutralHtml(), html);
        Assert.Equal("saved", await StateOf(_attemptId));
        Assert.Equal(1, _firstSync.Requests);
        Assert.Single(_consent.Accounts);
        Assert.Contains(_logs.Lines, l => l.Contains("Could not record the outcome") && l.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task A_choice_still_open_after_the_flows_own_deadline_is_kept_and_a_new_link_does_not_drop_it()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        _sessionId = "dkb-session-eeee5555";
        var state = await StartedState(new ConnectLinkRequest("DKB", "DE", null));
        var waiting = _attemptId;
        _clock.Now = T0.AddMinutes(29); // the bank answers late: the choice ends 14 minutes after the flow's own expiry
        Assert.Equal(200, Page(await Callback("the-code", state)).Status);

        _clock.Now = T0.AddMinutes(35);
        await CreateLink(new ConnectLinkRequest("ING", "DE", null));

        Assert.Equal("choose_account", await StateOf(waiting));
        Assert.NotNull((await RowAsync(waiting)).WaitingPayload);
        Assert.Empty(_deletes);
    }

    [Fact]
    public async Task A_new_link_closes_a_choice_that_ran_out_before_its_row_is_deleted()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        var old = _attemptId;

        _clock.Now = T0.AddHours(2);
        await CreateLink(new ConnectLinkRequest("ING", "DE", null));

        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        await using var db = NewDb();
        Assert.DoesNotContain(db.PendingConsents, p => p.Id == old);
    }

    [Fact]
    public async Task The_ticks_cleanup_closes_a_choice_that_ran_out_and_never_throws()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");
        var cleanup = new ConnectAttemptCleanup(
            new TestDbFactory(this), NewClient, _cipher, _clock, NullLogger<ConnectAttemptCleanup>.Instance);

        await cleanup.RunAsync(CancellationToken.None);
        Assert.Empty(_deletes); // before the deadline

        _clock.Now = T0.AddMinutes(16);
        await cleanup.RunAsync(CancellationToken.None);

        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);

        _deleteThrows = new HttpRequestException("down");
        await cleanup.RunAsync(CancellationToken.None); // nothing left to do, and no fault
    }

    private sealed class TestDbFactory(ConsentEndpointsTests owner) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => owner.NewDb();
    }

    [Fact]
    public async Task No_state_answer_holds_a_session_id_iban_or_account_name()
    {
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");

        var texts = new List<string> { (await AttemptState(_attemptId.ToString())).Text };
        _clock.Now = T0.AddMinutes(20);
        texts.Add((await AttemptState(_attemptId.ToString())).Text);

        foreach (var text in texts)
        {
            Assert.DoesNotContain("dkb-session-eeee5555", text);
            Assert.DoesNotContain(Iban1, text);
            Assert.DoesNotContain("Name u1", text);
            Assert.DoesNotContain("uid", text);
        }
    }

    private Guid _consentUser() => _consent.UserId;

    private void AssertNoSessionIdInLog()
    {
        foreach (var id in new[] { "old-session-aaaa1111", "new-session-bbbb2222" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(id));
        }
    }

    // --- Story 5.4: choose the account --------------------------------------------------------------

    private const string Iban3 = "DE00000000000000000003";

    private async Task<(int Status, string Text)> Reply(
        Func<GreedyNoseDbContext, Task<IResult>> call, bool lostRace = false, int? failSave = null, Action? afterFirstSave = null)
    {
        await using var db = NewDb(lostRace, failSave, afterFirstSave);
        var result = await call(db);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var text = result is IValueHttpResult { Value: not null } v ? JsonSerializer.Serialize(v.Value, JsonSerializerOptions.Web) : "";
        return (status, text);
    }

    private Task<(int Status, string Text)> AccountsOf(string id, Guid? user = null) =>
        Reply(db => ConnectChoice.AccountsAsync(id, new TestUser(user ?? SeedData.UserId), db, NewClient(), _cipher, _clock, _logs, CancellationToken.None));

    private Task<(int Status, string Text)> Choose(
        string id, string? accountId, bool lostRace = false, ConsentStore? store = null, Guid? user = null, int? failSave = null,
        CancellationTokenSource? hangUpAfterClaim = null) =>
        Reply(db => ConnectChoice.ChooseAsync(
            id, new ChoiceRequest(accountId), new TestUser(user ?? SeedData.UserId), db, NewClient(), _cipher, store ?? _consent, _firstSync,
            _clock, _logs, hangUpAfterClaim?.Token ?? CancellationToken.None), lostRace, failSave,
            hangUpAfterClaim is null ? null : () => hangUpAfterClaim.Cancel());

    private Task<(int Status, string Text)> CancelOf(string id, Guid? user = null, ConsentCipher? cipher = null) =>
        Reply(db => ConnectChoice.CancelAsync(id, new TestUser(user ?? SeedData.UserId), db, NewClient(), cipher ?? _cipher, _clock, _logs, CancellationToken.None));

    /// <summary>A bank with three euro accounts (savings, then two current ones) waiting for the choice; returns its attempt id.</summary>
    private async Task<string> ChoiceWaiting(string bank = "DKB", string session = "dkb-session-eeee5555")
    {
        OfferAccounts(
            AccountJson("u1", Iban1, "SVGS", usage: "PRIV"),
            AccountJson("u2", Iban2, "CACC"),
            AccountJson("u3", Iban3, "CACC"));
        var (status, _) = await ConnectBank(bank, session);
        Assert.Equal(200, status);
        return _attemptId.ToString();
    }

    private void AssertNoBankDataInLog()
    {
        Assert.DoesNotContain(_logs.Lines, l =>
            l.Contains(Iban1) || l.Contains(Iban2) || l.Contains(Iban3) || l.Contains("Name u") || l.Contains("dkb-session-eeee5555")
            || l.Contains("new-session-bbbb2222") || l.Contains("stub-uid"));
    }

    [Fact]
    public async Task The_list_has_one_row_per_account_with_exactly_the_allowed_fields_and_one_suggestion()
    {
        var id = await ChoiceWaiting();

        var (status, text) = await AccountsOf(id);

        Assert.Equal(200, status);
        var rows = JsonDocument.Parse(text).RootElement.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(["id", "name", "kind", "last4", "suggested"], r.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(["1", "2", "3"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(["savings", "current", "current"], rows.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal(["0001", "0002", "0003"], rows.Select(r => r.GetProperty("last4").GetString()));
        Assert.Equal("Name u2", rows[1].GetProperty("name").GetString());
        // The first current account carries the hint, and only that one.
        Assert.Equal([false, true, false], rows.Select(r => r.GetProperty("suggested").GetBoolean()));
        foreach (var secret in new[] { Iban1, Iban2, Iban3, "stub", "uid", "dkb-session-eeee5555", "PRIV", "Product" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public void The_name_falls_back_to_the_product_then_to_Account_and_the_kind_to_account()
    {
        var rows = ConnectChoice.ToRows(
        [
            new ConnectedAccount("a", "DE00 0000 0000 0000 0000 99", null, "EUR", null, "CARD", "  Gold \u0007Card  "),
            new ConnectedAccount("b", null, " ", "EUR", null, "LOAN", null),
            new ConnectedAccount("c", "DE12", new string('n', 300), "EUR", null, "weird", null),
            new ConnectedAccount("d", "DE0000000000000000000044", "x", "EUR", null, "cacc", null),
        ]);

        Assert.Equal(("Gold Card", "card", "0099"), (rows[0].Name, rows[0].Kind, rows[0].Last4));
        Assert.Equal(("Account", "loan", (string?)null), (rows[1].Name, rows[1].Kind, rows[1].Last4));
        Assert.Equal((100, "account", (string?)null), (rows[2].Name.Length, rows[2].Kind, rows[2].Last4));
        Assert.Equal(["False", "False", "False", "True"], rows.Select(r => r.Suggested.ToString()));
    }

    [Fact]
    public async Task Without_any_current_account_nothing_is_suggested()
    {
        OfferAccounts(AccountJson("u1", Iban1, "SVGS"), AccountJson("u2", Iban2, null));
        await ConnectBank("DKB", "dkb-session-eeee5555");

        var (_, text) = await AccountsOf(_attemptId.ToString());

        Assert.DoesNotContain("\"suggested\":true", text);
    }

    [Fact]
    public async Task Choosing_a_row_saves_the_bank_with_that_account_alone_queues_the_first_sync_and_ends_saved()
    {
        var id = await ChoiceWaiting();
        var before = _firstSync.Requests; // the callback queued nothing: nothing is saved yet

        var (status, text) = await Choose(id, "3");

        Assert.Equal(200, status);
        Assert.Equal("""{"state":"saved"}""", text);
        var account = Assert.Single(_consent.Accounts);
        Assert.Equal((Iban3, "u3"), (account.Iban, account.Uid));
        Assert.Equal("dkb-session-eeee5555", _consent.SessionIdFor(_consentUser(), "DKB"));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), _consent.ExpiresAt); // the bank's own valid_until, carried in the payload
        Assert.Equal(before + 1, _firstSync.Requests);
        Assert.Equal("saved", await StateOf(_attemptId));
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Empty(_deletes);
        AssertNoBankDataInLog();
    }

    [Fact]
    public async Task A_choice_for_a_reconnect_closes_the_old_session_after_the_new_one_is_stored()
    {
        await ConnectBank("DKB", "old-session-aaaa1111"); // one account: connected
        OfferAccounts(AccountJson("u1", Iban1, "CACC"), AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "new-session-bbbb2222"); // expired first (StartedState), several accounts: waits
        Assert.Empty(_deletes);

        Assert.Equal(200, (await Choose(_attemptId.ToString(), "2")).Status);

        var delete = Assert.Single(_deletes);
        Assert.Equal("/sessions/old-session-aaaa1111", delete.Path);
        Assert.Equal("new-session-bbbb2222", delete.StoredSessionAtCall);
        Assert.Equal(Iban2, Assert.Single(_consent.Accounts).Iban);
        AssertNoBankDataInLog();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("99")]
    [InlineData("u2")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_id_that_is_not_in_the_list_is_refused_with_400_and_nothing_changes(string? accountId)
    {
        var id = await ChoiceWaiting();
        var before = await RowAsync(_attemptId);

        var (status, _) = await Choose(id, accountId);

        Assert.Equal(400, status);
        Assert.Equal("choose_account", await StateOf(_attemptId));
        var after = await RowAsync(_attemptId);
        Assert.Equal(before.WaitingPayload, after.WaitingPayload);
        Assert.Equal(before.Version, after.Version);
        Assert.Empty(_consent.Accounts);
        Assert.Empty(_deletes);
    }

    [Fact]
    public async Task A_second_choice_is_refused_with_409_and_changes_nothing()
    {
        var id = await ChoiceWaiting();
        Assert.Equal(200, (await Choose(id, "2")).Status);
        var syncs = _firstSync.Requests;

        Assert.Equal(409, (await Choose(id, "3")).Status);

        Assert.Equal(Iban2, Assert.Single(_consent.Accounts).Iban);
        Assert.Equal(syncs, _firstSync.Requests);
        Assert.Equal("saved", await StateOf(_attemptId));
    }

    [Fact]
    public async Task Two_choices_at_once_cannot_both_win_the_loser_gets_409_and_nothing_is_saved_by_it()
    {
        var id = await ChoiceWaiting();

        var (status, _) = await Choose(id, "2", lostRace: true);

        Assert.Equal(409, status);
        Assert.Empty(_consent.Accounts);
        Assert.Equal("choose_account", await StateOf(_attemptId));
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
    }

    [Fact]
    public async Task A_choice_that_cannot_be_stored_closes_the_new_session_and_ends_failed()
    {
        var id = await ChoiceWaiting();
        var persistence = new FakeConsentPersistence { SaveFails = new InvalidOperationException("db down") };
        var failing = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);

        var (status, text) = await Choose(id, "2", store: failing);

        Assert.Equal(502, status);
        Assert.DoesNotContain("dkb-session", text);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        AssertNoBankDataInLog();
    }

    [Fact]
    public async Task A_choice_after_the_deadline_is_refused_with_409_the_session_closed_and_the_state_expired()
    {
        var id = await ChoiceWaiting();
        _clock.Now = T0.AddMinutes(16);

        Assert.Equal(409, (await Choose(id, "2")).Status);

        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Equal("expired", await StateOf(_attemptId));
        Assert.Empty(_consent.Accounts);
        Assert.Equal(409, (await AccountsOf(id)).Status);
        Assert.Equal(409, (await CancelOf(id)).Status);
    }

    [Fact]
    public async Task Attempts_in_other_states_have_no_list_no_choice_and_no_cancel()
    {
        await CreateToken(); // waiting
        var waiting = _attemptId.ToString();
        Assert.Equal(409, (await AccountsOf(waiting)).Status);
        Assert.Equal(409, (await Choose(waiting, "1")).Status);
        Assert.Equal(409, (await CancelOf(waiting)).Status);

        await StartedState();
        Page(await Callback("the-code", ReadState(_calls.Last(c => c.Path == "/auth").Body))); // one account: saved
        var saved = _attemptId.ToString();
        Assert.Equal("saved", await StateOf(_attemptId));
        Assert.Equal(409, (await AccountsOf(saved)).Status);
        Assert.Equal(409, (await Choose(saved, "1")).Status);
        Assert.Equal(409, (await CancelOf(saved)).Status);
        Assert.Equal("saved", await StateOf(_attemptId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_malformed_or_unknown_id_is_404_on_all_three_calls(string id)
    {
        Assert.Equal(404, (await AccountsOf(id)).Status);
        Assert.Equal(404, (await Choose(id, "1")).Status);
        Assert.Equal(404, (await CancelOf(id)).Status);
    }

    [Fact]
    public async Task Another_users_attempt_is_404_on_all_three_calls_and_unchanged()
    {
        var id = await ChoiceWaiting();
        var stranger = Guid.NewGuid();

        Assert.Equal(404, (await AccountsOf(id, stranger)).Status);
        Assert.Equal(404, (await Choose(id, "2", user: stranger)).Status);
        Assert.Equal(404, (await CancelOf(id, stranger)).Status);

        Assert.Equal("choose_account", await StateOf(_attemptId));
    }

    [Fact]
    public async Task Cancel_closes_the_waiting_session_deletes_the_payload_and_saves_nothing()
    {
        var id = await ChoiceWaiting();
        var syncs = _firstSync.Requests;

        var (status, text) = await CancelOf(id);

        Assert.Equal(200, status);
        Assert.Equal("""{"state":"failed","reason":"cancelled"}""", text);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Equal("cancelled", await ReasonOf(_attemptId));
        Assert.Empty(_consent.Accounts);
        Assert.Equal(syncs, _firstSync.Requests);
        // Leaving twice, or choosing after leaving, changes nothing.
        Assert.Equal(409, (await CancelOf(id)).Status);
        Assert.Equal(409, (await Choose(id, "2")).Status);
        Assert.Single(_deletes);
        AssertNoBankDataInLog();
    }

    [Fact]
    public async Task A_cancel_whose_close_fails_keeps_the_payload_and_the_cleanup_retries_it()
    {
        var id = await ChoiceWaiting();
        _deleteThrows = new HttpRequestException("down /sessions/dkb-session-eeee5555");

        var (status, _) = await CancelOf(id);

        Assert.Equal(200, status); // the owner has left; the session is the backend's business
        Assert.NotNull((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Equal("cancelled", await ReasonOf(_attemptId));
        Assert.Empty(_consent.Accounts);

        _deleteThrows = null;
        _clock.Now = T0 + ConnectAttempts.CloseBackoff; // the read above already tried and failed once more
        await StateOf(_attemptId); // a state read runs the cleanup: a failed row that still holds a payload is due

        Assert.Equal(3, _deletes.Count); // the cancel, the read inside ReasonOf, this read
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        AssertNoBankDataInLog();
    }

    [Fact]
    public void A_payload_written_before_the_valid_until_was_added_still_opens()
    {
        var id = Guid.NewGuid();
        var old = """{"SessionId":"old-s","Accounts":[{"Uid":"u","Iban":"DE00","Name":null,"Currency":"EUR","IdentificationHash":null}]}""";
        var sealedOld = _cipher.Encrypt(Encoding.UTF8.GetBytes(old), $"{id:D}|WaitingPayload");

        var choice = WaitingPayload.Open(_cipher, id, sealedOld);

        Assert.Equal("old-s", choice.SessionId);
        Assert.Null(choice.ValidUntil);
        Assert.Single(choice.Accounts);
    }

    [Fact]
    public async Task A_choice_from_an_old_payload_is_saved_without_the_banks_end_date()
    {
        var id = await ChoiceWaiting();
        await using (var db = NewDb())
        {
            var row = await db.PendingConsents.SingleAsync(p => p.Id == _attemptId);
            var choice = WaitingPayload.Open(_cipher, row.Id, row.WaitingPayload!);
            row.WaitingPayload = WaitingPayload.Seal(_cipher, row.Id, choice with { ValidUntil = null });
            await db.SaveChangesAsync();
        }

        Assert.Equal(200, (await Choose(id, "2")).Status);

        Assert.Equal(Iban2, Assert.Single(_consent.Accounts).Iban);
    }

    [Theory]
    [InlineData("DE00 0000 0000 0000 0001", "0001")]
    [InlineData("de89370400440532013abc", "3ABC")]
    [InlineData("DE123456", "3456")]
    [InlineData("DE12345", null)]
    [InlineData("1234", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Last4_is_upper_case_and_never_a_whole_short_value(string? iban, string? expected)
    {
        Assert.Equal(expected, ConnectChoice.Last4(iban));
    }

    [Fact]
    public async Task A_client_that_hangs_up_right_after_the_claim_still_ends_with_the_consent_stored()
    {
        var id = await ChoiceWaiting();
        var probe = new ConsentStore(new ProbePersistence(new FakeConsentPersistence()), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        using var hangUp = new CancellationTokenSource();

        var (status, _) = await Choose(id, "2", store: probe, hangUpAfterClaim: hangUp);

        Assert.True(hangUp.IsCancellationRequested);
        Assert.Equal(200, status);
        Assert.Equal(Iban2, Assert.Single(probe.Accounts).Iban);
        Assert.Equal("saved", await StateOf(_attemptId));
        Assert.Empty(_deletes);
    }

    [Fact]
    public async Task An_unexpected_fault_after_the_claim_closes_the_new_session_and_never_leaves_saved_without_a_consent()
    {
        var id = await ChoiceWaiting();
        var probe = new ConsentStore(
            new ProbePersistence(new FakeConsentPersistence()) { LoadSessionThrows = new OperationCanceledException("SECRET dkb-session-eeee5555") },
            TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);

        var (status, text) = await Choose(id, "2", store: probe);

        Assert.Equal(502, status);
        Assert.DoesNotContain("SECRET", text);
        Assert.Empty(probe.Accounts);
        Assert.Equal("/sessions/dkb-session-eeee5555", Assert.Single(_deletes).Path);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("SECRET"));
        AssertNoBankDataInLog();
    }

    [Fact]
    public async Task A_failing_first_sync_request_does_not_undo_a_stored_consent()
    {
        var id = await ChoiceWaiting();
        _firstSync.Throws = new InvalidOperationException("queue down Host=secret");

        var (status, _) = await Choose(id, "2");

        Assert.Equal(200, status);
        Assert.Equal(Iban2, Assert.Single(_consent.Accounts).Iban);
        Assert.Equal("saved", await StateOf(_attemptId));
        Assert.Empty(_deletes);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("secret"));
    }

    [Fact]
    public async Task A_failing_write_of_the_failed_outcome_after_a_store_failure_is_retried_so_the_row_does_not_stay_saved()
    {
        var id = await ChoiceWaiting();
        var persistence = new FakeConsentPersistence { SaveFails = new InvalidOperationException("db down") };
        var failing = new ConsentStore(persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);

        // Saves of this call: 1 is the claim, 2 records "failed" (fails), 3 is the retry.
        var (status, _) = await Choose(id, "2", store: failing, failSave: 2);

        Assert.Equal(502, status);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
    }

    [Fact]
    public async Task A_cancel_with_an_unreadable_payload_deletes_it_without_a_bank_call()
    {
        var id = await ChoiceWaiting();

        var (status, _) = await CancelOf(id, cipher: new ConsentCipher(RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(200, status);
        Assert.Empty(_deletes);
        Assert.Null((await RowAsync(_attemptId)).WaitingPayload);
        Assert.Equal("cancelled", await ReasonOf(_attemptId));
        Assert.Contains(_logs.Lines, l => l.Contains("could not be read") && l.Contains("ConsentDecryptionException"));
        AssertNoBankDataInLog();
    }

    [Fact]
    public async Task A_callback_whose_old_session_read_throws_closes_the_new_session_once_and_ends_failed()
    {
        var probe = new ConsentStore(
            new ProbePersistence(new FakeConsentPersistence()) { LoadSessionThrows = new TaskCanceledException("SECRET command timeout") },
            TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        _sessionId = "new-session-bbbb2222";
        var state = await StartedState(new ConnectLinkRequest("DKB", "DE", null));
        await using var db = NewDb();

        var result = await ConsentEndpoints.CallbackAsync(
            "the-code", state, null, db, NewClient(), _cipher, probe, _firstSync, _clock, _logs, CancellationToken.None);

        var (status, html) = Page(result);
        Assert.Equal(502, status);
        Assert.Equal(await NeutralHtml(), html);
        Assert.Equal("/sessions/new-session-bbbb2222", Assert.Single(_deletes).Path);
        Assert.Empty(probe.Accounts);
        Assert.Equal(0, _firstSync.Requests);
        Assert.Equal("failed", await StateOf(_attemptId));
        Assert.Equal("store-failed", await ReasonOf(_attemptId));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("SECRET"));
        AssertNoSessionIdInLog();
    }

    [Fact]
    public async Task An_account_without_an_iban_is_listed_without_last4_and_kept_under_its_identification_hash()
    {
        // Pins the existing key rule (ConnectedAccount.Key: IBAN, else identification hash, else uid); it is not changed here.
        OfferAccounts(
            """{"uid":"u1","name":"No IBAN","currency":"EUR","cash_account_type":"CACC","identification_hash":"hash-1"}""",
            AccountJson("u2", Iban2, "CACC"));
        await ConnectBank("DKB", "dkb-session-eeee5555");

        var (_, list) = await AccountsOf(_attemptId.ToString());
        var first = JsonDocument.Parse(list).RootElement.GetProperty("accounts")[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("last4").ValueKind);

        Assert.Equal(200, (await Choose(_attemptId.ToString(), "1")).Status);

        var account = Assert.Single(_consent.Accounts);
        Assert.Null(account.Iban);
        Assert.Equal("hash-1", account.Key);
    }

    [Fact]
    public async Task The_callback_keeps_the_banks_valid_until_in_the_payload()
    {
        await ChoiceWaiting();

        var row = await RowAsync(_attemptId);

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), WaitingPayload.Open(_cipher, row.Id, row.WaitingPayload!).ValidUntil);
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
