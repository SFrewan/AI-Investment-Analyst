using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

public sealed class ConfidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Values_in_the_closed_unit_interval_are_accepted(decimal value) =>
        Assert.Equal(value, Confidence.Create(value).Value);

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(100)]
    public void Values_outside_the_unit_interval_are_rejected(decimal value) =>
        Assert.Throws<DomainValidationException>(() => Confidence.Create(value));

    [Theory]
    [InlineData(0.05, ConfidenceBand.VeryLow)]
    [InlineData(0.30, ConfidenceBand.Low)]
    [InlineData(0.50, ConfidenceBand.Moderate)]
    [InlineData(0.75, ConfidenceBand.High)]
    [InlineData(0.95, ConfidenceBand.VeryHigh)]
    public void Bands_partition_the_interval(decimal value, ConfidenceBand expected) =>
        Assert.Equal(expected, Confidence.Create(value).Band);

    [Fact]
    public void IsAtLeast_compares_by_value()
    {
        Assert.True(Confidence.Create(0.8m).IsAtLeast(Confidence.Create(0.7m)));
        Assert.False(Confidence.Create(0.6m).IsAtLeast(Confidence.Create(0.7m)));
    }

    [Fact]
    public void Equality_is_by_value() =>
        Assert.Equal(Confidence.Create(0.75m), Confidence.Create(0.75m));
}
