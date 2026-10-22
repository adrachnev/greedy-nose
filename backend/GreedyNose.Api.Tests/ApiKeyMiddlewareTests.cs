using GreedyNose.Api.Auth;
using GreedyNose.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 1.3: the API key gate (shared by every host, here through the Functions middleware), run against <see cref="DefaultHttpContext"/> — no server. The key
/// below is a throwaway test value, not a real secret.
/// </summary>
public class ApiKeyMiddlewareTests
{
    private const string Key = "test-key-0123456789-abcdefghijklmnopqrstuvwxyz";

    private sealed class Harness
    {
        public bool NextCalled { get; private set; }
        public CapturingLogger Logger { get; } = new();
        private readonly ApiKeyFunctionsMiddleware _middleware;

        public Harness()
        {
            _middleware = new ApiKeyFunctionsMiddleware(new ApiKeyGate(new ApiKeyOptions { Key = Key }), Logger);
        }

        public async Task<HttpContext> SendAsync(string path, string? authorization)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            if (authorization is not null)
            {
                context.Request.Headers.Authorization = authorization;
            }

            await _middleware.InvokeAsync(context, () => { NextCalled = true; return Task.CompletedTask; });
            return context;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-key-wrong-key-wrong-key-wrong-key")]
    [InlineData("Basic " + Key)]
    [InlineData("Bearer ")]
    [InlineData("Bearer")]
    [InlineData("Bearer  " + Key)]
    [InlineData("Bearer " + Key + " ")]
    [InlineData(Key)]
    public async Task Invalid_authorization_gets_401_and_next_is_not_called(string? header)
    {
        var h = new Harness();

        var context = await h.SendAsync("/debits", header);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.False(h.NextCalled);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData("Bearer " + Key)]
    [InlineData("bearer " + Key)]
    [InlineData("BEARER " + Key)]
    public async Task Right_key_calls_next(string header)
    {
        var h = new Harness();

        var context = await h.SendAsync("/debits", header);

        Assert.True(h.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/connect")]
    [InlineData("/connect/")]
    [InlineData("/callback")]
    public async Task Exempt_paths_pass_without_key(string path)
    {
        var h = new Harness();

        await h.SendAsync(path, null);

        Assert.True(h.NextCalled);
    }

    [Theory]
    [InlineData("/connect-links")]
    [InlineData("/connect-attempts/0b6a1d2e-5c3f-4a8b-9e7d-1f2a3b4c5d6e")]
    [InlineData("/connectx")]
    [InlineData("/callbackx")]
    [InlineData("/connect/../health")]
    [InlineData("/connect/health")]
    [InlineData("/callback/../debits")]
    [InlineData("/health")]
    [InlineData("/status")]
    [InlineData("/raw")]
    [InlineData("/aspsps")]
    [InlineData("/banks")]
    [InlineData("/does-not-exist")]
    public async Task Other_paths_without_key_get_401(string path)
    {
        var h = new Harness();

        var context = await h.SendAsync(path, null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(h.NextCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key-wrong-key-wrong-key-wrong-key")]
    public async Task Status_without_or_with_a_wrong_key_gets_401(string? header)
    {
        var h = new Harness();

        var context = await h.SendAsync("/status", header);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(h.NextCalled);
    }

    [Fact]
    public async Task Status_with_the_right_key_calls_next()
    {
        var h = new Harness();

        await h.SendAsync("/status", "Bearer " + Key);

        Assert.True(h.NextCalled);
    }

    [Fact]
    public async Task The_key_never_appears_in_log_messages()
    {
        var h = new Harness();

        await h.SendAsync("/debits", "Bearer " + Key);
        await h.SendAsync("/debits", "Bearer " + Key + "x");
        await h.SendAsync("/health", "Basic " + Key);
        await h.SendAsync("/debits", null);

        Assert.NotEmpty(h.Logger.Entries);
        Assert.DoesNotContain(Key, h.Logger.All);
        Assert.Contains("/debits", h.Logger.All);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-key")]
    [InlineData("0123456789012345678901234567890")] // 31 characters
    public void Options_validation_throws_for_missing_or_short_key(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new ApiKeyOptions { Key = key }.Validate());
    }

    [Fact]
    public async Task Key_differing_only_in_case_gets_401()
    {
        var h = new Harness();

        var context = await h.SendAsync("/debits", "Bearer " + Key.ToUpperInvariant());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(h.NextCalled);
    }

    [Theory]
    [InlineData("/CONNECT")]
    [InlineData("/Callback")]
    public async Task Exempt_paths_match_case_insensitively_like_routing(string path)
    {
        var h = new Harness();

        await h.SendAsync(path, null);

        Assert.True(h.NextCalled);
    }

    [Fact]
    public async Task Logged_path_is_sanitized_and_truncated()
    {
        var h = new Harness();
        var path = "/a\r\nFORGED" + new string('x', 300);

        await h.SendAsync(path, null);

        var logged = Assert.Single(h.Logger.Entries);
        Assert.DoesNotContain('\r', logged);
        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain(new string('x', 150), logged);
        Assert.Contains("...", logged);
    }

    [Theory]
    [InlineData(" 0123456789012345678901234567890123456789")]
    [InlineData("0123456789012345678901234567890123456789\t")]
    public void Options_validation_rejects_edge_whitespace_without_echoing_the_key(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ApiKeyOptions { Key = key }.Validate());

        Assert.Contains("whitespace", ex.Message);
        Assert.DoesNotContain(key.Trim(), ex.Message);
    }

    [Fact]
    public void Options_validation_accepts_a_32_character_key()
    {
        new ApiKeyOptions { Key = new string('k', 32) }.Validate();
    }

    private sealed class CapturingLogger : ILogger<ApiKeyFunctionsMiddleware>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var arguments = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";

            Entries.Add($"{formatter(state, exception)} | {arguments}");
        }

        public string All => string.Join("\n", Entries);
    }
}
