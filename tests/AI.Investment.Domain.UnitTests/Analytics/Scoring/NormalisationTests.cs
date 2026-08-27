using System.Globalization;
using AI.Investment.Domain.Analytics.Scoring;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Scoring;

public sealed class NormalisationTests
{
    // Inputs arrive as strings and are parsed invariantly. decimal is not a legal attribute
    // argument type, so a decimal theory parameter relies on an implied double conversion that
    // both loses exactness and is not guaranteed to survive the xUnit analyzer.
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.125", "0.5")]
    [InlineData("0.25", "1")]
    public void A_value_is_placed_on_the_declared_range(string raw, string expected) =>
        Assert.Equal(D(expected), Normalisation.Between(0m, 0.25m).Apply(D(raw)));

    /// <summary>
    /// One extraordinary figure must not be able to overwhelm every other component, which is what
    /// extrapolating past the top of the range would let it do.
    /// </summary>
    [Theory]
    [InlineData("-5", "0")]
    [InlineData("0.9", "1")]
    [InlineData("1000", "1")]
    public void Values_outside_the_range_clamp_rather_than_extrapolate(string raw, string expected) =>
        Assert.Equal(D(expected), Normalisation.Between(0m, 0.25m).Apply(D(raw)));

    /// <summary>Leverage: two is the bad end, and the range simply runs downwards.</summary>
    [Theory]
    [InlineData("0", "1")]
    [InlineData("1", "0.5")]
    [InlineData("2", "0")]
    [InlineData("5", "0")]
    public void A_range_running_downwards_means_lower_is_better(string raw, string expected)
    {
        var normalisation = Normalisation.Between(2m, 0m);

        Assert.True(normalisation.LowerIsBetter);
        Assert.Equal(D(expected), normalisation.Apply(D(raw)));
    }

    [Fact]
    public void An_upward_range_does_not_claim_lower_is_better() =>
        Assert.False(Normalisation.Between(0m, 1m).LowerIsBetter);

    /// <summary>A range of zero width lets a component appear to count while contributing nothing.</summary>
    [Fact]
    public void A_range_of_zero_width_is_refused() =>
        Assert.Throws<DomainValidationException>(() => Normalisation.Between(1m, 1m));
}
