using GreedyNose.Api.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The device-token contract (NOTIFICATION-TRACER-BULLET.md, step 2) as far as it can be pinned
/// without a database. The upsert itself — including two concurrent posts of one token — is what the
/// project's no-database rule leaves to a run against real Postgres.
/// </summary>
public class DeviceTokenTests
{
    // Shaped like a real FCM token (~160 chars of [A-Za-z0-9:_-]) without being one.
    private static readonly string FakeToken = "fake-" + new string('a', 60) + ":APA91b_" + new string('B', 60) + "-9";

    [Fact]
    public void A_normal_token_is_accepted()
    {
        Assert.True(DeviceTokenValidation.TryValidate(FakeToken, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void The_first_and_last_printable_ASCII_characters_are_accepted()
    {
        // The range edges: an off-by-one at either end would reject '!' or '~'.
        Assert.True(DeviceTokenValidation.TryValidate("!~", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \t\r\n ")]
    public void A_missing_or_blank_token_is_rejected(string? token)
    {
        Assert.False(DeviceTokenValidation.TryValidate(token, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void A_token_of_exactly_the_maximum_length_is_accepted()
    {
        Assert.True(DeviceTokenValidation.TryValidate(new string('a', DeviceTokenValidation.MaxLength), out _));
    }

    [Fact]
    public void A_token_one_character_over_the_maximum_is_rejected()
    {
        Assert.False(DeviceTokenValidation.TryValidate(new string('a', DeviceTokenValidation.MaxLength + 1), out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>
    /// Each of these would pass a "not blank, not too long" check and then fail inside Postgres or
    /// at send time: a NUL cannot be stored in <c>text</c> at all (22021), and whitespace or
    /// non-ASCII never occurs in a token FCM issued. Rejecting them here turns a 500 into a 400.
    /// </summary>
    [Theory]
    [InlineData("abc def")]
    [InlineData("abc\tdef")]
    [InlineData("abc\ndef")]
    [InlineData("abc\rdef")]
    [InlineData("abc\0def")]
    [InlineData("abc\u007fdef")] // DEL, the one control character just above the printable range
    [InlineData("tökén")]
    [InlineData("abc😀def")]
    public void A_token_with_a_space_control_or_non_ASCII_character_is_rejected(string token)
    {
        Assert.False(DeviceTokenValidation.TryValidate(token, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>
    /// The reason "store exactly as sent, never silently trim" still holds, now made stricter:
    /// a padded token is rejected outright instead of being accepted or quietly rewritten into a
    /// string FCM never issued.
    /// </summary>
    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("  both  ")]
    public void A_padded_token_is_rejected_not_trimmed(string padded)
    {
        Assert.False(DeviceTokenValidation.TryValidate(padded, out _));
        Assert.True(DeviceTokenValidation.TryValidate(padded.Trim(), out _), "The trimmed form is fine — the padding is what is refused.");
    }

    /// <summary>
    /// Proves two things for each kind of invalid input: the handler answers 400 problem details, and
    /// it does so before the database is touched — the context is null, so a handler that reached for
    /// it would throw instead of returning. (Whether the body echoes the token is the next test's job.)
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has a space")]
    public async Task An_invalid_token_gets_a_400_problem_without_touching_the_database(string? token)
    {
        var result = await DeviceTokenEndpoint.HandleAsync(
            new DeviceTokenRequest(token), db: null!, TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    /// <summary>The same guarantee for a request with no body at all (the binder hands us <c>null</c>).</summary>
    [Fact]
    public async Task A_missing_body_gets_a_400_problem_without_touching_the_database()
    {
        var result = await DeviceTokenEndpoint.HandleAsync(
            request: null, db: null!, TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    /// <summary>
    /// A rejected token is still a credential-shaped string the caller sent; the 400 must not reflect
    /// it back. Covers both rejection paths that have something to echo: too long, and bad characters.
    /// </summary>
    [Fact]
    public async Task A_rejected_token_is_not_echoed_back_in_the_400()
    {
        var tooLong = new string('q', DeviceTokenValidation.MaxLength + 1);
        var badCharacters = "secret token with spaces";

        foreach (var token in new[] { tooLong, badCharacters })
        {
            var result = await DeviceTokenEndpoint.HandleAsync(
                new DeviceTokenRequest(token), db: null!, TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);

            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(problem.ProblemDetails));
        }
    }
}
