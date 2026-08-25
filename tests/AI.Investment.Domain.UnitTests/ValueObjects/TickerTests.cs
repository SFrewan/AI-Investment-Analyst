using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

public sealed class TickerTests
{
    [Theory]
    [InlineData("msft", "MSFT")]
    [InlineData("  aapl  ", "AAPL")]
    [InlineData("BRK.B", "BRK.B")]
    [InlineData("brk-b", "BRK-B")]
    [InlineData("7203", "7203")]
    public void Create_normalises_to_upper_case_and_trims(string input, string expected) =>
        Assert.Equal(expected, Ticker.Create(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_missing_value(string? input) =>
        Assert.Throws<DomainValidationException>(() => Ticker.Create(input!));

    [Theory]
    [InlineData("THISISWAYTOOLONG")]
    [InlineData("MS FT")]
    [InlineData("MS$FT")]
    [InlineData("this is a company name")]
    [InlineData(".MSFT")]
    [InlineData("MSFT.")]
    [InlineData("-MSFT")]
    public void Create_rejects_malformed_symbols(string input) =>
        Assert.Throws<DomainValidationException>(() => Ticker.Create(input));

    [Fact]
    public void Equality_is_by_value_and_survives_case_differences() =>
        Assert.Equal(Ticker.Create("msft"), Ticker.Create("MSFT"));

    [Fact]
    public void Equal_tickers_share_a_hash_code() =>
        Assert.Equal(Ticker.Create("msft").GetHashCode(), Ticker.Create("MSFT").GetHashCode());

    [Fact]
    public void Different_tickers_are_not_equal() =>
        Assert.NotEqual(Ticker.Create("MSFT"), Ticker.Create("AAPL"));

    [Fact]
    public void TryCreate_returns_false_instead_of_throwing_for_prose()
    {
        Assert.False(Ticker.TryCreate("Microsoft Corporation", out var ticker));
        Assert.Null(ticker);
    }

    [Fact]
    public void TryCreate_succeeds_for_a_well_formed_symbol()
    {
        Assert.True(Ticker.TryCreate("msft", out var ticker));
        Assert.Equal("MSFT", ticker!.Value);
    }
}
