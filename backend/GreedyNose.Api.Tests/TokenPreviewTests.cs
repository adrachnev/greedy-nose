using GreedyNose.Api.Notifications;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The only forms of a device token that may reach a log line or a response body. Shared by
/// registration, sending and the debug endpoint, so its tests are not any one of theirs.
/// </summary>
public class TokenPreviewTests
{
    // Shaped like a real FCM token — well over 32 characters, and with the ':' a real one carries, which
    // is what makes a URL-encoded copy differ from the original.
    private const string FakeToken = "fake-token-1234567890:APA91b_abcdefghijklmnopqrstuvwxyz-XYZ";

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(163)]
    [InlineData(1024)]
    public void The_log_preview_never_contains_the_whole_token(int length)
    {
        var token = new string('x', length);

        var preview = TokenPreview.Of(token);

        Assert.DoesNotContain(token, preview);
        Assert.True(preview.Length <= 7, "Preview should be a short prefix plus an ellipsis.");
    }

    [Fact]
    public void The_log_preview_of_a_real_length_token_still_tells_registrations_apart()
    {
        Assert.Equal("fcm-ab…", TokenPreview.Of("fcm-ab" + new string('z', 150)));
    }

    [Fact]
    public void Redact_replaces_every_occurrence_of_the_token_with_its_preview()
    {
        var text = $"Bad token {FakeToken}; again {FakeToken}.";

        var redacted = TokenPreview.Redact(text, FakeToken);

        Assert.DoesNotContain(FakeToken, redacted);
        Assert.Equal($"Bad token {TokenPreview.Of(FakeToken)}; again {TokenPreview.Of(FakeToken)}.", redacted);
    }

    /// <summary>The one variant beyond the verbatim token that it promises to catch: the URL-encoded form (<c>:</c> becomes <c>%3A</c>).</summary>
    [Fact]
    public void Redact_also_replaces_the_url_encoded_token()
    {
        var encoded = Uri.EscapeDataString(FakeToken);
        Assert.NotEqual(FakeToken, encoded); // otherwise this test would prove nothing

        var redacted = TokenPreview.Redact($"GET /send?to={encoded}", FakeToken);

        Assert.DoesNotContain(encoded, redacted);
        Assert.Equal($"GET /send?to={TokenPreview.Of(FakeToken)}", redacted);
    }

    [Fact]
    public void Redact_leaves_text_without_the_token_alone()
    {
        const string text = "The registration token is not a valid FCM registration token";

        Assert.Equal(text, TokenPreview.Redact(text, FakeToken));
        Assert.Equal(text, TokenPreview.Redact(text, ""));
    }
}
