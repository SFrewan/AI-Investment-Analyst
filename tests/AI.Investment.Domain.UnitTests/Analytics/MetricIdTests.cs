using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class MetricIdTests
{
    [Fact]
    public void An_identifier_is_normalised_and_states_its_family()
    {
        var metric = MetricId.Create("  Financial.Revenue.Growth  ");

        Assert.Equal("financial.revenue.growth", metric.Value);
        Assert.Equal("financial", metric.Family);
        Assert.Equal("financial.revenue.growth", metric.ToString());
    }

    /// <summary>
    /// The rule that keeps the platform from becoming a stock analyser: a bare name is ambiguous
    /// the moment a second domain measures growth of something else.
    /// </summary>
    [Theory]
    [InlineData("growth")]
    [InlineData("revenue")]
    public void A_metric_must_name_the_family_it_belongs_to(string value)
    {
        var exception = Assert.Throws<DomainValidationException>(() => MetricId.Create(value));

        Assert.Contains("segments", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_identifier_is_required(string? value) =>
        Assert.Throws<DomainValidationException>(() => MetricId.Create(value!));

    [Theory]
    [InlineData("financial revenue.growth")]
    [InlineData("financial.revenue_growth")]
    [InlineData("financial.revenue/growth")]
    public void Only_letters_digits_hyphen_and_the_separator_are_accepted(string value) =>
        Assert.Throws<DomainValidationException>(() => MetricId.Create(value));

    [Theory]
    [InlineData(".financial.revenue")]
    [InlineData("financial.revenue.")]
    [InlineData("financial..revenue")]
    public void No_segment_may_be_empty(string value) =>
        Assert.Throws<DomainValidationException>(() => MetricId.Create(value));

    [Fact]
    public void An_identifier_has_a_length_limit() =>
        Assert.Throws<DomainValidationException>(
            () => MetricId.Create("financial." + new string('a', MetricId.MaxLength)));

    /// <summary>Hyphens survive, because multi-word measures are ordinary.</summary>
    [Fact]
    public void Hyphenated_segments_are_accepted()
    {
        var metric = MetricId.Create("market.free-cash-flow.margin");

        Assert.Equal("market.free-cash-flow.margin", metric.Value);
        Assert.Equal("market", metric.Family);
    }

    [Fact]
    public void Two_identifiers_naming_the_same_measure_are_equal() =>
        Assert.Equal(MetricId.Create("Financial.Revenue"), MetricId.Create("financial.revenue"));
}
