using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class CalculationVersionTests
{
    [Fact]
    public void A_version_round_trips_through_its_stored_form()
    {
        var version = CalculationVersion.Create(2, 7);

        Assert.Equal("v2.7", version.ToString());
        Assert.Equal(version, CalculationVersion.Parse("v2.7"));
        Assert.Equal(version, CalculationVersion.Parse("2.7"));
        Assert.Equal(version, CalculationVersion.Parse("  V2.7 "));
    }

    /// <summary>
    /// Version 0 is how "nobody versioned this" is usually spelled, which is the exact state the
    /// type exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_version_starts_at_one(int major) =>
        Assert.Throws<DomainValidationException>(() => CalculationVersion.Create(major, 0));

    [Fact]
    public void A_minor_version_may_not_be_negative() =>
        Assert.Throws<DomainValidationException>(() => CalculationVersion.Create(1, -1));

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("1.2.3")]
    [InlineData("v1.x")]
    [InlineData("-1.0")]
    public void An_unreadable_version_is_refused(string value) =>
        Assert.Throws<DomainValidationException>(() => CalculationVersion.Parse(value));

    [Fact]
    public void A_newer_formula_supersedes_an_older_one()
    {
        var oldest = CalculationVersion.Create(1, 0);
        var newerMinor = CalculationVersion.Create(1, 4);
        var newerMajor = CalculationVersion.Create(2, 0);

        Assert.True(newerMinor.IsNewerThan(oldest));
        Assert.True(newerMajor.IsNewerThan(newerMinor));
        Assert.False(oldest.IsNewerThan(newerMinor));
        Assert.False(oldest.IsNewerThan(oldest));
    }
}
