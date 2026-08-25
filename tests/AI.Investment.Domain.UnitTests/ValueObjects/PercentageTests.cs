using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

public sealed class PercentageTests
{
    [Fact]
    public void FromRatio_and_FromPercent_describe_the_same_proportion() =>
        Assert.Equal(Percentage.FromRatio(0.155m), Percentage.FromPercent(15.5m));

    [Fact]
    public void Percent_is_the_ratio_scaled_by_one_hundred() =>
        Assert.Equal(15.5m, Percentage.FromRatio(0.155m).Percent);

    [Fact]
    public void Negative_proportions_are_allowed_because_margins_and_returns_can_be_negative() =>
        Assert.Equal(-0.25m, Percentage.FromRatio(-0.25m).Ratio);

    /// <summary>
    /// The guard against the classic unit-confusion bug: passing 15 to FromRatio meaning
    /// "15 per cent" would silently mean 1500 per cent.
    /// </summary>
    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    [InlineData(1000)]
    public void FromRatio_rejects_values_that_are_almost_certainly_percent_values(decimal ratio) =>
        Assert.Throws<DomainValidationException>(() => Percentage.FromRatio(ratio));

    [Fact]
    public void Zero_is_zero() => Assert.Equal(0m, Percentage.Zero.Ratio);

    [Fact]
    public void Equality_is_by_value() =>
        Assert.Equal(Percentage.FromPercent(10m), Percentage.FromPercent(10m));
}
