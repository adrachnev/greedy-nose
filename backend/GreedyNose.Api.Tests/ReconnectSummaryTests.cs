using GreedyNose.Api.Ingestion;

namespace GreedyNose.Api.Tests;

public class ReconnectSummaryTests
{
    [Theory]
    [InlineData(3, 2, "3 new debits while you were disconnected", "2 of them are bad. Tap to review them.")]
    [InlineData(12, 12, "12 new debits while you were disconnected", "12 of them are bad. Tap to review them.")]
    // Verbatim R20, no pluralisation: "1 new debits" is the owner's call for REQUIREMENTS.md (TODO.md).
    [InlineData(1, 1, "1 new debits while you were disconnected", "1 of them are bad. Tap to review them.")]
    public void Build_follows_R20_wording_verbatim(int n, int m, string title, string body)
    {
        Assert.Equal((title, body), ReconnectSummary.Build(n, m));
    }
}
