using System.Globalization;
using AI.Investment.Domain.Ai.Groundedness;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ai;

public sealed class NumericTextScannerTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no digits at all here")]
    public void Prose_without_numerals_yields_no_mentions(string? text) =>
        Assert.Empty(NumericTextScanner.Scan(text));

    [Fact]
    public void A_plain_number_is_found()
    {
        var mention = Assert.Single(NumericTextScanner.Scan("revenue was 1000 for the period"));

        Assert.Equal("1000", mention.Text);
        Assert.Contains(1000m, mention.Candidates);
    }

    [Fact]
    public void Thousands_separators_and_decimals_are_read()
    {
        var mention = Assert.Single(NumericTextScanner.Scan("it reached 1,234.56 overall"));

        Assert.Contains(D("1234.56"), mention.Candidates);
    }

    /// <summary>
    /// A percentage is quoted in points and stored as a ratio, so both readings must be admissible -
    /// otherwise every correct answer is rejected and the tolerance gets widened to compensate.
    /// </summary>
    [Theory]
    [InlineData("margin of 18.4%")]
    [InlineData("margin of 18.4 percent")]
    public void A_percentage_offers_both_the_points_and_the_ratio(string text)
    {
        var mention = Assert.Single(NumericTextScanner.Scan(text));

        Assert.Contains(D("18.4"), mention.Candidates);
        Assert.Contains(D("0.184"), mention.Candidates);
    }

    [Theory]
    [InlineData("1.2 billion", "1200000000")]
    [InlineData("1.2bn", "1200000000")]
    [InlineData("3 million", "3000000")]
    [InlineData("4k", "4000")]
    [InlineData("2 trillion", "2000000000000")]
    public void A_scale_suffix_offers_the_scaled_value(string text, string expected)
    {
        var mention = Assert.Single(NumericTextScanner.Scan(text));

        Assert.Contains(D(expected), mention.Candidates);
    }

    /// <summary>
    /// Without the word-boundary check, "3 monthly" reads as three million and the validator starts
    /// accepting numbers nobody wrote.
    /// </summary>
    [Fact]
    public void A_suffix_only_counts_when_the_word_ends_there()
    {
        var mention = Assert.Single(NumericTextScanner.Scan("3 monthly filings"));

        Assert.Equal(3m, Assert.Single(mention.Candidates));
    }

    /// <summary>
    /// A date is not a negative number. Without this, every sentence mentioning one is reported for
    /// quoting a figure that does not exist.
    /// </summary>
    [Fact]
    public void A_hyphen_between_digits_is_not_a_minus_sign()
    {
        var mentions = NumericTextScanner.Scan("published 2026-02-10");

        Assert.Equal(3, mentions.Count);
        Assert.All(mentions, mention => Assert.All(mention.Candidates, value => Assert.True(value >= 0m)));
    }

    [Fact]
    public void A_leading_minus_sign_is_read_when_it_really_is_one()
    {
        var mention = Assert.Single(NumericTextScanner.Scan("a swing of -300 in the period"));

        Assert.Contains(-300m, mention.Candidates);
    }

    [Fact]
    public void Several_numbers_in_one_sentence_are_all_found() =>
        Assert.Equal(3, NumericTextScanner.Scan("revenue 1000, income 100, margin 10%").Count);
}
