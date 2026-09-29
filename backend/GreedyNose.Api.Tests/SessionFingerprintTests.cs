using GreedyNose.Api.Ingestion;

namespace GreedyNose.Api.Tests;

public class SessionFingerprintTests
{
    [Fact]
    public void The_same_input_always_gives_the_same_64_char_lowercase_hex()
    {
        var first = SessionFingerprint.Of("session-abc");

        Assert.Equal(first, SessionFingerprint.Of("session-abc"));
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Different_inputs_give_different_fingerprints()
    {
        Assert.NotEqual(SessionFingerprint.Of("session-a"), SessionFingerprint.Of("session-b"));
    }

    [Fact]
    public void The_fingerprint_does_not_contain_the_input()
    {
        const string sessionId = "0123456789abcdef";

        Assert.DoesNotContain(sessionId, SessionFingerprint.Of(sessionId));
    }

    [Fact]
    public void Matches_a_known_SHA256_vector()
    {
        // SHA-256("abc"), FIPS 180-4 test vector.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SessionFingerprint.Of("abc"));
    }
}
