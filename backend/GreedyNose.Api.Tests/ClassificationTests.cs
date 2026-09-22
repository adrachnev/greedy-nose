using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The stored form of a classification. Needs no database: the converter is a pure function, and
/// what it must guarantee is that the column holds exactly the app's <c>good</c>/<c>bad</c> —
/// not the C# member names, and never a silent default for something it does not recognise.
/// </summary>
public class ClassificationTests
{
    private readonly ClassificationConverter _converter = new();

    [Theory]
    [InlineData(Classification.Good, "good")]
    [InlineData(Classification.Bad, "bad")]
    public void Converter_stores_the_apps_lowercase_text_and_reads_it_back(Classification classification, string text)
    {
        Assert.Equal(text, _converter.ConvertToProvider(classification));
        Assert.Equal(classification, _converter.ConvertFromProvider(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    // The enum's member names are not the wire format — a value written by HasConversion<string>()
    // would look like this, and reading it as valid would hide the mismatch.
    [InlineData("Good")]
    [InlineData("BAD")]
    public void Converter_refuses_a_stored_value_it_does_not_recognise(string stored)
    {
        Assert.Throws<FormatException>(() => _converter.ConvertFromProvider(stored));
    }

    [Fact]
    public void Converter_refuses_an_enum_value_that_is_not_a_member()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _converter.ConvertToProvider((Classification)99));
    }

    [Theory]
    [InlineData("good", Classification.Good)]
    [InlineData("bad", Classification.Bad)]
    public void TryParse_reads_the_apps_lowercase_text(string text, Classification expected)
    {
        Assert.True(ClassificationText.TryParse(text, out var classification));
        Assert.Equal(expected, classification);
    }

    /// <summary>
    /// The client-facing counterpart to <c>Converter_refuses_a_stored_value_it_does_not_recognise</c>
    /// above: same bad inputs, but <see cref="ClassificationText.TryParse"/> must answer <c>false</c>
    /// instead of throwing — a client's bad <c>POST /rules</c> body is a 400, not a 500.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("Good")]
    [InlineData("BAD")]
    public void TryParse_returns_false_for_anything_it_does_not_recognise(string? text)
    {
        Assert.False(ClassificationText.TryParse(text, out _));
    }
}
