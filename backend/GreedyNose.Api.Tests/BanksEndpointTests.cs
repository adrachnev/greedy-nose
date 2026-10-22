using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 5.2: <c>GET /banks?q=</c> through its Function, against a stub in place of Enable Banking (no network, no database).
/// The 409 on a second consent for a connected bank is tested in <see cref="ConsentEndpointsTests"/>.
/// </summary>
public sealed class BanksEndpointTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private const string SentinelSecret = "SENTINEL-TEST-CREDENTIAL";
    private const string SearchText = "sentinel-search-text";

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"banks-key-{Guid.NewGuid():N}.pem");
    private readonly EnableBankingSigner _signer;
    private readonly FakeClock _clock = new(T0);
    private readonly FakeConsentPersistence _persistence = new();
    private readonly ConsentStore _consent;
    private readonly List<string> _requests = [];
    private readonly List<string> _logLines = [];

    private (HttpStatusCode Status, string Body) _list = (HttpStatusCode.OK, List());
    private Exception? _listThrows;

    // A test can hold every list request open (a gate) to make parallel searches really overlap.
    private TaskCompletionSource? _gate;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string List() => $$"""
        {"aspsps":[
          {"name":"Zeta Bank","country":"DE","credentials":[{"secret":"{{SentinelSecret}}"}]},
          {"name":"alpha bank","country":"FR"},
          {"name":"Alpha Bank","country":"DE"},
          {"name":"Beta Sparkasse","country":"DE"},
          {"name":"No Country"},
          {"country":"DE"}]}
        """;

    public BanksEndpointTests()
    {
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(_keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        var options = new EnableBankingOptions { ApplicationId = "test-app", PrivateKeyPath = _keyPath };
        _signer = new EnableBankingSigner(options);
        _consent = new ConsentStore(_persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        Catalogue = NewCatalogue();
    }

    public void Dispose()
    {
        _signer.Dispose();
        File.Delete(_keyPath);
    }

    private BankCatalogue Catalogue { get; }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(BanksEndpointTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (owner._requests)
            {
                owner._requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            }

            owner._started.TrySetResult();
            if (owner._gate is { } gate)
            {
                await gate.Task;
            }

            if (owner._listThrows is { } fault)
            {
                throw fault;
            }

            return new HttpResponseMessage(owner._list.Status) { Content = new StringContent(owner._list.Body) };
        }
    }

    private sealed class Capture(List<string> lines) : ILogger<BankCatalogue>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
    }

    private BankCatalogue NewCatalogue() => new(
        () => new EnableBankingClient(
            new HttpClient(new StubHandler(this), disposeHandler: true) { BaseAddress = new Uri("https://eb.example") },
            _signer, _clock),
        _clock, new Capture(_logLines));

    private BanksFunction Function(BankCatalogue? catalogue = null) =>
        new(new OwnerCurrentUser(), _consent, catalogue ?? Catalogue, _clock, new Capture(_logLines));

    private async Task<(int Status, string Body)> SearchAsync(string? q, BankCatalogue? catalogue = null, CancellationToken ct = default)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        if (q is not null)
        {
            context.Request.QueryString = new QueryString("?q=" + Uri.EscapeDataString(q));
        }

        var result = await Function(catalogue).Run(context.Request, ct);
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static List<(string Name, string Country, bool Connected)> Hits(string body) =>
        [.. JsonDocument.Parse(body).RootElement.GetProperty("banks").EnumerateArray().Select(b =>
            (b.GetProperty("name").GetString()!, b.GetProperty("country").GetString()!, b.GetProperty("connected").GetBoolean()))];

    private Task ConnectAsync(string aspsp, DateTimeOffset expiresAt, Guid? user = null) =>
        _consent.CompleteAsync(user ?? SeedData.UserId, "SESSION-" + aspsp, aspsp,
            [new ConnectedAccount("uid", "DE00TESTIBAN", "Owner", "EUR", null)], T0.AddDays(-10), expiresAt);

    [Fact]
    public async Task Hits_are_case_insensitive_contains_sorted_by_name_then_country()
    {
        var (status, body) = await SearchAsync("  BANK ");

        Assert.Equal(200, status);
        // Same name ignoring case: the country decides (DE before FR).
        Assert.Equal(
            [("Alpha Bank", "DE"), ("alpha bank", "FR"), ("Zeta Bank", "DE")],
            Hits(body).Select(h => (h.Name, h.Country)));
    }

    [Fact]
    public async Task The_answer_holds_only_name_country_and_connected()
    {
        var (_, body) = await SearchAsync("zeta");

        var bank = JsonDocument.Parse(body).RootElement.GetProperty("banks")[0];
        Assert.Equal(["name", "country", "connected"], bank.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(SentinelSecret, body);
    }

    [Fact]
    public async Task At_most_twenty_banks_are_returned()
    {
        _list = (HttpStatusCode.OK, "{\"aspsps\":[" + string.Join(",", Enumerable.Range(0, 30).Select(i => $"{{\"name\":\"Many {i:D2}\",\"country\":\"DE\"}}")) + "]}");

        var hits = Hits((await SearchAsync("many")).Body);

        Assert.Equal(20, hits.Count);
        Assert.Equal("Many 00", hits[0].Name);
        Assert.Equal("Many 19", hits[^1].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  a  ")]
    [InlineData("   ")]
    public async Task A_text_shorter_than_two_characters_is_refused_without_reading_the_list(string? q)
    {
        var (status, body) = await SearchAsync(q);

        Assert.Equal(400, status);
        Assert.Contains("\"q\"", body);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_text_longer_than_a_hundred_characters_is_refused()
    {
        Assert.Equal(400, (await SearchAsync(new string('a', 101))).Status);
        Assert.Equal(200, (await SearchAsync(new string('a', 100))).Status);
    }

    [Fact]
    public async Task No_match_is_an_empty_list_not_an_error()
    {
        var (status, body) = await SearchAsync("nothing like this");

        Assert.Equal(200, status);
        Assert.Empty(Hits(body));
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{\"other\":[]}")]
    public async Task An_unreadable_list_is_502_and_not_cached(int status, string body)
    {
        _list = ((HttpStatusCode)status, body);

        var failed = await SearchAsync("bank");

        Assert.Equal(502, failed.Status);
        Assert.Contains("Bank list unavailable", failed.Body);

        _list = (HttpStatusCode.OK, List());
        Assert.Equal(200, (await SearchAsync("bank")).Status);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task A_network_fault_is_502()
    {
        _listThrows = new HttpRequestException("Host=secret-host");

        var (status, body) = await SearchAsync("bank");

        Assert.Equal(502, status);
        Assert.DoesNotContain("secret-host", body);
        Assert.DoesNotContain(_logLines, l => l.Contains("secret-host"));
    }

    [Fact]
    public async Task A_bank_with_a_consent_is_marked_connected_in_any_state_and_name_case_is_ignored()
    {
        await ConnectAsync("ALPHA BANK", T0.AddDays(40));
        await ConnectAsync("Zeta Bank", T0.AddDays(-1)); // expired still has a card in Settings
        await ConnectAsync("Beta Sparkasse", T0.AddDays(40), user: Guid.Parse("0b6c1a52-7d3e-4f0a-9c1e-5a2d8e4f6b71"));

        var hits = Hits((await SearchAsync("bank")).Body);

        Assert.Equal(2, hits.Count(h => h.Name.Equals("alpha bank", StringComparison.OrdinalIgnoreCase) && h.Connected));
        Assert.True(hits.Single(h => h.Name == "Zeta Bank").Connected);
        Assert.False(Hits((await SearchAsync("sparkasse")).Body).Single().Connected); // another user's bank
    }

    [Fact]
    public async Task A_cold_host_restores_the_consents_before_marking()
    {
        await ConnectAsync("Zeta Bank", T0.AddDays(40));
        var cold = new ConsentStore(_persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        var function = new BanksFunction(new OwnerCurrentUser(), cold, Catalogue, _clock, new Capture(_logLines));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Request.QueryString = new QueryString("?q=zeta");

        await (await function.Run(context.Request, CancellationToken.None)).ExecuteResultAsync(new ActionContext { HttpContext = context });
        context.Response.Body.Position = 0;

        Assert.True(Hits(await new StreamReader(context.Response.Body).ReadToEndAsync()).Single().Connected);
    }

    [Fact]
    public async Task A_failing_consent_read_is_503_without_details()
    {
        _persistence.LoadFails = new InvalidOperationException("Host=secret-host Password=secret");

        var (status, body) = await SearchAsync("bank");

        Assert.Equal(503, status);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain(_logLines, l => l.Contains("secret"));
    }

    [Fact]
    public async Task The_list_is_read_once_for_ten_minutes_and_then_again()
    {
        await SearchAsync("bank");
        await SearchAsync("zeta");
        _clock.Now = T0 + BankCatalogue.CacheLifetime - TimeSpan.FromSeconds(1);
        await SearchAsync("alpha");
        Assert.Single(_requests);

        _clock.Now = T0 + BankCatalogue.CacheLifetime;
        await SearchAsync("bank");

        Assert.Equal(2, _requests.Count);
        Assert.All(_requests, r => Assert.Equal("GET /aspsps", r));
    }

    private async Task WaitForListRequestAsync() => await _started.Task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Parallel_searches_that_really_overlap_read_the_list_once()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var searches = Enumerable.Range(0, 8).Select(_ => SearchAsync("bank")).ToList();
        await WaitForListRequestAsync();
        await Task.Delay(100); // let the others queue behind the open read
        Assert.All(searches, s => Assert.False(s.IsCompleted));

        _gate.SetResult();
        var answers = await Task.WhenAll(searches);

        Assert.All(answers, a => Assert.Equal(200, a.Status));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Parallel_searches_during_a_failure_share_one_read_and_all_get_502_then_the_next_reads_again()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _list = (HttpStatusCode.InternalServerError, "{}");
        var searches = Enumerable.Range(0, 8).Select(_ => SearchAsync("bank")).ToList();
        await WaitForListRequestAsync();
        await Task.Delay(100);

        _gate.SetResult();
        var answers = await Task.WhenAll(searches);

        Assert.All(answers, a => Assert.Equal(502, a.Status));
        Assert.Single(_requests);

        _list = (HttpStatusCode.OK, List());
        Assert.Equal(200, (await SearchAsync("bank")).Status);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_break_the_others_or_the_refresh()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var first = SearchAsync("bank", ct: cts.Token);
        var others = Enumerable.Range(0, 3).Select(_ => SearchAsync("bank")).ToList();
        await WaitForListRequestAsync();
        await Task.Delay(100);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _gate.SetResult();
        var answers = await Task.WhenAll(others);

        Assert.All(answers, a => Assert.Equal(200, a.Status));
        // The refresh finished and was cached; nothing is left hanging, so the next search needs no read.
        Assert.Equal(200, (await SearchAsync("bank")).Status);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task A_cancelled_only_caller_leaves_no_stuck_refresh()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var only = SearchAsync("bank", ct: cts.Token);
        await WaitForListRequestAsync();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        _gate.SetResult();

        // The abandoned read completes on its own and is cached; a later search is served without blocking.
        var next = await SearchAsync("bank").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, next.Status);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task A_second_search_does_not_restore_the_consents_again_but_a_cold_host_restores_first()
    {
        await SearchAsync("bank");
        Assert.Equal(1, _persistence.LoadCalls);

        await SearchAsync("zeta");

        Assert.Equal(1, _persistence.LoadCalls);
    }

    [Fact]
    public async Task A_failed_restore_is_tried_again_by_the_next_search()
    {
        _persistence.LoadFails = new InvalidOperationException("down");
        Assert.Equal(503, (await SearchAsync("bank")).Status);

        _persistence.LoadFails = null;
        Assert.Equal(200, (await SearchAsync("bank")).Status);
        Assert.Equal(2, _persistence.LoadCalls);
    }

    [Theory]
    [InlineData("societe", "Société Générale")]
    [InlineData("SOCIÉTÉ", "Société Générale")]
    [InlineData("uber", "Über Bank")]
    [InlineData("strasse", "Straßen Bank")]
    [InlineData("kobenhavn", "København Bank")]
    [InlineData("lodz", "Łódź Bank")]
    public async Task Search_ignores_accents(string q, string expectedName)
    {
        _list = (HttpStatusCode.OK, """
            {"aspsps":[{"name":"Société Générale","country":"FR"},{"name":"Über Bank","country":"DE"},
            {"name":"Straßen Bank","country":"DE"},{"name":"København Bank","country":"DK"},{"name":"Łódź Bank","country":"PL"}]}
            """);

        var names = Hits((await SearchAsync(q)).Body).Select(h => h.Name).ToList();

        Assert.Equal([expectedName], names);
    }

    [Fact]
    public async Task A_name_with_a_lone_surrogate_never_fails_the_search()
    {
        // JSON escape for a lone high surrogate: the name cannot be normalized.
        _list = (HttpStatusCode.OK, "{\"aspsps\":[{\"name\":\"Odd \\ud800 Bank\",\"country\":\"DE\"},{\"name\":\"Société Bank\",\"country\":\"FR\"}]}");

        var (status, body) = await SearchAsync("societe");
        Assert.Equal(200, status);
        Assert.Equal(["Société Bank"], Hits(body).Select(h => h.Name));

        // The JSON reader refuses the odd name itself, so that one bank is skipped and the rest of the list stays.
        Assert.Equal(["Société Bank"], Hits((await SearchAsync("bank")).Body).Select(h => h.Name));
        // A lone surrogate in the search text itself is no 500 either.
        Assert.Equal(200, (await SearchAsync("ab\ud800")).Status);
    }

    [Theory]
    [InlineData("ab\ud800")]
    [InlineData("\udc00x")]
    public void Fold_returns_text_with_a_lone_surrogate_unchanged(string text)
    {
        Assert.Equal(text, GreedyNose.Api.Banks.SearchFold.Fold(text));
    }

    [Fact]
    public async Task A_property_name_with_a_lone_surrogate_does_not_fail_the_search()
    {
        _list = (HttpStatusCode.OK, "{\"aspsps\":[{\"na\\ud800me\":\"x\",\"name\":\"Ok Bank\",\"country\":\"DE\"},{\"name\":\"Fine Bank\",\"country\":\"FR\"}]}");

        var (status, body) = await SearchAsync("bank");

        Assert.Equal(200, status);
        Assert.Equal(["Fine Bank", "Ok Bank"], Hits(body).Select(h => h.Name));
    }

    [Fact]
    public async Task Hits_are_still_sorted_by_the_real_name()
    {
        _list = (HttpStatusCode.OK, """{"aspsps":[{"name":"Zürich Bank","country":"CH"},{"name":"Zeta Bank","country":"DE"}]}""");

        Assert.Equal(["Zeta Bank", "Zürich Bank"], Hits((await SearchAsync("bank")).Body).Select(h => h.Name));
    }

    [Fact]
    public async Task The_search_text_is_never_logged()
    {
        await SearchAsync(SearchText);
        _list = (HttpStatusCode.InternalServerError, "{}");
        await SearchAsync(SearchText, NewCatalogue());

        Assert.NotEmpty(_logLines);
        Assert.DoesNotContain(_logLines, l => l.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logLines, l => l.Contains(SentinelSecret));
    }
}
