using System.Net;
using GreedyNose.Api.EnableBanking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Tests;

/// <summary>
/// gn-6yh: no Enable Banking session id or account uid may reach the logs. The one line we keep is
/// <c>Enable Banking {Method} {PathTemplate} -> {StatusCode}</c>; no network, no database.
/// </summary>
public sealed class RedactedRequestLogTests
{
    private const string SessionId = "3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f7a14";
    private const string AccountUid = "a1b2c3d4-e5f6-4789-8abc-def012345678";
    private const string Secret = "supersecretvalue";

    // --- Template ---------------------------------------------------------------------------------

    [Theory]
    [InlineData($"https://api.enablebanking.com/sessions/{SessionId}", "/sessions/{id}")]
    [InlineData($"https://api.enablebanking.com/accounts/{AccountUid}/transactions?date_from=2026-01-01&continuation_key=abc", "/accounts/{id}/transactions")]
    [InlineData("https://api.enablebanking.com/aspsps?country=DE", "/aspsps")]
    [InlineData("https://api.enablebanking.com/auth", "/auth")]
    [InlineData("https://api.enablebanking.com/accounts/AbCdEfGhIjKlMnOpQrStUvWx12/balances", "/accounts/{id}/balances")]
    [InlineData("https://api.enablebanking.com/accounts/abc123/balances", "/accounts/{id}/balances")]
    [InlineData("https://api.enablebanking.com/sessions/%41%42%43", "/sessions/{id}")]
    [InlineData("https://api.enablebanking.com/sessions/dGVzdHNlc3Npb24=", "/sessions/{id}")]
    [InlineData("https://api.enablebanking.com/sessions/foobar", "/sessions/{id}")]
    [InlineData("https://api.enablebanking.com/", "/")]
    public void PathTemplate_replaces_id_segments_and_drops_the_query(string url, string expected) =>
        Assert.Equal(expected, RedactedRequestLogHandler.PathTemplate(new Uri(url)));

    [Fact]
    public void PathTemplate_handles_a_relative_uri()
    {
        var uri = new Uri($"/sessions/{SessionId}?x={Secret}", UriKind.Relative);
        Assert.Equal("/sessions/{id}", RedactedRequestLogHandler.PathTemplate(uri));
    }

    // --- Handler ----------------------------------------------------------------------------------

    private sealed record Line(string Category, LogLevel Level, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<Line> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, List<Line> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // The exception itself is rendered too: a handler that logs it would show up here.
                lines.Add(new Line(category, logLevel, formatter(state, exception) + exception));
            }
        }
    }

    private sealed class FakeInner(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpMessageInvoker Invoker(CapturingProvider logs, HttpMessageHandler inner)
    {
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var handler = new RedactedRequestLogHandler(factory.CreateLogger<RedactedRequestLogHandler>()) { InnerHandler = inner };
        return new HttpMessageInvoker(handler);
    }

    [Fact]
    public async Task The_line_has_method_template_and_status_but_never_the_id_the_query_or_a_header()
    {
        var logs = new CapturingProvider();
        var invoker = Invoker(logs, new FakeInner(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.enablebanking.com/accounts/{AccountUid}/transactions?date_from={Secret}");
        request.Headers.Add("Authorization", $"Bearer {Secret}");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var line = Assert.Single(logs.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Enable Banking GET /accounts/{id}/transactions -> 429", line.Message);
        Assert.DoesNotContain(AccountUid, line.Message);
        Assert.DoesNotContain(Secret, line.Message);
    }

    [Fact]
    public async Task A_thrown_exception_logs_its_type_only_and_is_rethrown()
    {
        var logs = new CapturingProvider();
        var invoker = Invoker(logs, new FakeInner(_ =>
            throw new HttpRequestException($"Connection failed for https://api.enablebanking.com/sessions/{SessionId}")));
        var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.enablebanking.com/sessions/{SessionId}");

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        var line = Assert.Single(logs.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("DELETE /sessions/{id}", line.Message);
        Assert.Contains(nameof(HttpRequestException), line.Message);
        Assert.DoesNotContain(SessionId, line.Message);
    }

    // --- DI wiring --------------------------------------------------------------------------------

    private sealed class ConnectionFreeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static IServiceCollection CoreServices(CapturingProvider logs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=db;Username=u;Password=p",
                ["Consent:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddGreedyNoseCore(config);

        // Replace the real network handler; the rest of each client (BaseAddress, our handler, the
        // default-logger removal) stays exactly as AddGreedyNoseCore built it.
        services.AddHttpClient(nameof(EnableBankingClient)).ConfigurePrimaryHttpMessageHandler(() => new ConnectionFreeHandler());
        services.AddHttpClient(GreedyNose.Api.Alerts.BrevoAlertMailSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ConnectionFreeHandler());
        return services;
    }

    // Fails if ConfigureHttpClientDefaults(RemoveAllLoggers) is dropped: the default logger writes
    // "Sending HTTP request GET <full url>" under System.Net.Http.HttpClient.* categories.
    [Fact]
    public async Task An_Enable_Banking_client_logs_only_the_redacted_line_and_no_default_HttpClient_line()
    {
        var logs = new CapturingProvider();
        using var provider = CoreServices(logs).BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EnableBankingClient));

        using var response = await client.GetAsync($"/accounts/{AccountUid}/transactions?date_from=2026-01-01");

        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains(AccountUid));
        var line = Assert.Single(logs.Lines, l => l.Category == typeof(RedactedRequestLogHandler).FullName);
        Assert.Equal("Enable Banking GET /accounts/{id}/transactions -> 200", line.Message);
    }

    [Fact]
    public async Task The_Brevo_client_has_no_default_HttpClient_log_either()
    {
        var logs = new CapturingProvider();
        using var provider = CoreServices(logs).BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GreedyNose.Api.Alerts.BrevoAlertMailSender.HttpClientName);

        using var response = await client.GetAsync("/v3/smtp/email");

        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }

}
