using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 1.8: every tick sends date_from and pages to the end.
/// Against a fake <see cref="HttpMessageHandler"/> and a throwaway signing key — no network.
/// </summary>
public sealed class EnableBankingFirstSyncPagingTests : IDisposable
{
    private static readonly ConnectedAccount Account = new("uid-1", "DE11222233334444555566", null, "EUR", null);
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly DateFrom = new(2025, 9, 22);

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"eb-test-key-{Guid.NewGuid():N}.pem");
    private readonly EnableBankingOptions _options;
    private readonly EnableBankingSigner _signer;

    public EnableBankingFirstSyncPagingTests()
    {
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(_keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        _options = new EnableBankingOptions { ApplicationId = "test-app", PrivateKeyPath = _keyPath };
        _signer = new EnableBankingSigner(_options);
    }

    public void Dispose()
    {
        _signer.Dispose();
        File.Delete(_keyPath);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            return Task.FromResult(respond(Queries.Count - 1, request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private static HttpResponseMessage Page(string? continuationKey, params EbTransaction[] transactions) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new EbTransactionsResponse(transactions, continuationKey), EnableBankingClient.Json)),
        };

    private static EbTransaction Booked(string date, string name, string amount = "10.00", string? entryReference = null) =>
        new(
            EntryReference: entryReference,
            TransactionAmount: new EbAmount("EUR", amount),
            Creditor: new EbParty(name),
            CreditorAccount: null,
            CreditorAgent: null,
            BankTransactionCode: new EbBankTransactionCode("PMNT", "CCRD", "POSD"),
            CreditDebitIndicator: "DBIT",
            Status: "BOOK",
            BookingDate: date,
            ValueDate: null,
            TransactionDate: null,
            RemittanceInformation: []);

    private EnableBankingDebitsFetcher NewFetcher(StubHandler handler) =>
        new(new StubHttpClientFactory(handler, new Uri(_options.BaseUrl)), _signer, new FixedTimeProvider(Now),
            _options, NullLogger<EnableBankingDebitsFetcher>.Instance);

    [Fact]
    public async Task A_fetch_sends_date_from_and_follows_the_keys_across_three_pages()
    {
        const string key1 = "a+b/c=1";
        const string key2 = "second key";
        var handler = new StubHandler((i, _) => i switch
        {
            0 => Page(key1, Booked("2026-09-01", "SHOP A", entryReference: "e1")),
            1 => Page(key2, Booked("2026-06-01", "SHOP B", entryReference: "e2")),
            _ => Page(null, Booked("2025-10-01", "SHOP C", entryReference: "e3")),
        });

        var mapped = await NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None);

        Assert.Equal(3, handler.Queries.Count);
        Assert.Equal("?date_from=2025-09-22", handler.Queries[0]);
        Assert.Equal($"?date_from=2025-09-22&continuation_key={Uri.EscapeDataString(key1)}", handler.Queries[1]);
        Assert.Equal($"?date_from=2025-09-22&continuation_key={Uri.EscapeDataString(key2)}", handler.Queries[2]);
        Assert.Equal(3, mapped.Payload.Debits.Count);
        Assert.Equal(3, mapped.Payload.Payees.Count);
    }

    [Theory]
    [InlineData(2026, 3, 24)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 12, 31)]
    public async Task Any_tick_sends_the_given_date_from_unchanged_and_pages_to_the_end(int year, int month, int day)
    {
        var from = new DateOnly(year, month, day);
        var handler = new StubHandler((i, _) => i == 0
            ? Page("more", Booked("2026-09-20", "SHOP A", entryReference: "e1"))
            : Page(null, Booked("2026-09-21", "SHOP B", entryReference: "e2")));

        var mapped = await NewFetcher(handler).FetchAsync(Account, from, CancellationToken.None);

        var expected = $"?date_from={year:0000}-{month:00}-{day:00}";
        Assert.Equal([expected, $"{expected}&continuation_key=more"], handler.Queries);
        Assert.Equal(2, mapped.Payload.Debits.Count);
    }

    [Fact]
    public async Task A_short_history_succeeds_with_one_page()
    {
        var handler = new StubHandler((_, _) => Page(null, Booked("2026-09-20", "SHOP A")));

        var mapped = await NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None);

        Assert.Single(handler.Queries);
        Assert.Single(mapped.Payload.Debits);
    }

    [Fact]
    public async Task An_empty_key_string_ends_the_paging()
    {
        var handler = new StubHandler((_, _) => Page("", Booked("2026-09-20", "SHOP A")));

        await NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None);

        Assert.Single(handler.Queries);
    }

    [Fact]
    public async Task A_failing_second_page_throws_with_the_bank_error_code_and_returns_nothing()
    {
        var handler = new StubHandler((i, _) => i == 0
            ? Page("k1", Booked("2026-09-01", "SHOP A"))
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"code":429,"message":"slow down","error":"ASPSP_RATE_LIMIT_EXCEEDED"}"""),
            });

        var exception = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal("ASPSP_RATE_LIMIT_EXCEEDED", exception.ErrorCode);
        Assert.Equal(2, handler.Queries.Count);
    }

    [Fact]
    public async Task A_repeated_continuation_key_throws_instead_of_looping()
    {
        var handler = new StubHandler((_, _) => Page("same", Booked("2026-09-01", "SHOP A")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None));

        Assert.Equal(2, handler.Queries.Count);
    }

    [Fact]
    public async Task Endless_fresh_keys_stop_at_the_page_cap()
    {
        var handler = new StubHandler((i, _) => Page($"key-{i}", Booked("2026-09-01", "SHOP A")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None));

        Assert.Equal(200, handler.Queries.Count);
    }

    [Fact]
    public async Task Identical_charges_split_across_a_page_boundary_get_distinct_ids_equal_to_one_mapping_of_all_of_them()
    {
        // Same day, payee and amount, no entry_reference: only the ordinal tells them apart.
        var first = Booked("2026-09-01", "SHOP A");
        var second = Booked("2026-09-01", "SHOP A");
        var third = Booked("2026-09-01", "SHOP A");
        var handler = new StubHandler((i, _) => i == 0 ? Page("k1", first, second) : Page(null, third));

        var mapped = await NewFetcher(handler).FetchAsync(Account, DateFrom, CancellationToken.None);

        var ids = mapped.Payload.Debits.Select(d => d.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
        var expected = TransactionMapper.Map(new EbTransactionsResponse([first, second, third], null), Account.Key);
        Assert.Equal(expected.Payload.Debits.Select(d => d.Id), ids);
    }
}
