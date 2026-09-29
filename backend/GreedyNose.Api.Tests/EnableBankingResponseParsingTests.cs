using System.Net;
using System.Security.Cryptography;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// What the production fetcher and status checker make of Enable Banking's answers (R19), against a
/// fake <see cref="HttpMessageHandler"/> — no network. The bodies are the ones measured on the sandbox
/// on 2026-09-29.
/// </summary>
public sealed class EnableBankingResponseParsingTests : IDisposable
{
    private const string ClosedSessionBody = """{"code":401,"message":"Session is closed","error":"CLOSED_SESSION"}""";
    private const string WrongSignatureBody = """{"code":401,"message":"Wrong signature"}""";

    private static readonly ConnectedAccount Account = new("uid-1", "DE11222233334444555566", null, "EUR", null);

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"eb-test-key-{Guid.NewGuid():N}.pem");
    private readonly EnableBankingOptions _options;
    private readonly EnableBankingSigner _signer;

    public EnableBankingResponseParsingTests()
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private (StubHandler Handler, IHttpClientFactory Factory) Stub(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return (handler, new StubHttpClientFactory(handler, new Uri(_options.BaseUrl)));
    }

    private EnableBankingDebitsFetcher NewFetcher(IHttpClientFactory factory) =>
        new(factory, _signer, TimeProvider.System, _options, NullLogger<EnableBankingDebitsFetcher>.Instance);

    private EnableBankingSessionStatusChecker NewChecker(IHttpClientFactory factory) =>
        new(factory, _signer, TimeProvider.System, NullLogger<EnableBankingSessionStatusChecker>.Instance);

    // --- Fetcher: the typed exception -------------------------------------------------------------

    [Fact]
    public async Task A_401_with_an_error_field_carries_the_status_and_the_error_code_but_not_the_body()
    {
        var (_, factory) = Stub(HttpStatusCode.Unauthorized, ClosedSessionBody);

        var exception = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewFetcher(factory).FetchAsync(Account, CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("CLOSED_SESSION", exception.ErrorCode);
        Assert.DoesNotContain("Session is closed", exception.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task A_401_without_an_error_field_wrong_signature_has_no_error_code()
    {
        var (_, factory) = Stub(HttpStatusCode.Unauthorized, WrongSignatureBody);

        var exception = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewFetcher(factory).FetchAsync(Account, CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.DoesNotContain("Wrong signature", exception.Message);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"error":42}""")]
    public async Task An_unreadable_or_oddly_shaped_error_body_means_no_error_code(string body)
    {
        var (_, factory) = Stub(HttpStatusCode.BadGateway, body);

        var exception = await Assert.ThrowsAsync<EnableBankingRequestException>(() =>
            NewFetcher(factory).FetchAsync(Account, CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
    }

    // --- Status checker ---------------------------------------------------------------------------

    [Fact]
    public async Task The_checker_reads_the_status_of_the_session()
    {
        var (handler, factory) = Stub(HttpStatusCode.OK, """{"session_id":"abc","status":"CLOSED","access":{"valid_until":"2026-12-01T00:00:00Z"}}""");

        var status = await NewChecker(factory).GetStatusAsync("abc", CancellationToken.None);

        Assert.Equal("CLOSED", status);
        Assert.Equal("/sessions/abc", Assert.Single(handler.Paths));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("""{"session_id":"abc"}""")]
    [InlineData("""{"status":7}""")]
    [InlineData("""{"status":""}""")]
    public async Task The_checker_answers_unknown_for_a_broken_or_status_less_body(string body)
    {
        var (_, factory) = Stub(HttpStatusCode.OK, body);

        Assert.Null(await NewChecker(factory).GetStatusAsync("abc", CancellationToken.None));
    }

    [Fact]
    public async Task The_checker_answers_unknown_for_a_non_2xx_answer_even_if_the_body_names_a_status()
    {
        var (_, factory) = Stub(HttpStatusCode.InternalServerError, """{"status":"CLOSED"}""");

        Assert.Null(await NewChecker(factory).GetStatusAsync("abc", CancellationToken.None));
    }

    [Fact]
    public async Task The_checker_answers_unknown_when_the_call_itself_fails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var factory = new StubHttpClientFactory(handler, new Uri(_options.BaseUrl));

        Assert.Null(await NewChecker(factory).GetStatusAsync("abc", CancellationToken.None));
    }
}
