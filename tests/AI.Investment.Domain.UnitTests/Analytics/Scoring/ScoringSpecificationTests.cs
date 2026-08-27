using System.Globalization;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Analytics.Scoring;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Scoring;

public sealed class ScoringSpecificationTests
{
    private static ScoreComponent Component(MetricId metric, decimal weight = 1m) =>
        ScoreComponent.Create(metric, weight, Normalisation.Between(0m, 1m));

    private static ScoringSpecification Specification(
        IEnumerable<ScoreComponent> components,
        decimal minimumCoverage = 0.5m) =>
        ScoringSpecification.Create(
            MetricId.Create("score.test"),
            CalculationVersion.Create(1, 0),
            components,
            minimumCoverage,
            "a specification used by tests");

    [Fact]
    public void A_specification_totals_its_weights()
    {
        var specification = Specification(
        [
            Component(FinancialMetrics.NetMargin, 2m),
            Component(FinancialMetrics.CurrentRatio, 3m),
        ]);

        Assert.Equal(5m, specification.TotalWeight);
        Assert.Equal(2, specification.Components.Count);
    }

    /// <summary>Listing a metric twice weights it by stealth.</summary>
    [Fact]
    public void A_metric_may_count_only_once()
    {
        var exception = Assert.Throws<DomainValidationException>(() => Specification(
        [
            Component(FinancialMetrics.NetMargin),
            Component(FinancialMetrics.NetMargin),
        ]));

        Assert.Contains(FinancialMetrics.NetMargin.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_score_with_no_components_measures_nothing() =>
        Assert.Throws<DomainValidationException>(() => Specification(Array.Empty<ScoreComponent>()));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_component_weight_must_be_positive(string weight) =>
        Assert.Throws<DomainValidationException>(
            () => ScoreComponent.Create(
                FinancialMetrics.NetMargin,
                decimal.Parse(weight, CultureInfo.InvariantCulture),
                Normalisation.Between(0m, 1m)));

    /// <summary>A floor of zero would let a score be reported from no evidence at all.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("1.5")]
    public void Required_coverage_must_be_above_zero_and_at_most_one(string coverage) =>
        Assert.Throws<DomainValidationException>(
            () => Specification(
                [Component(FinancialMetrics.NetMargin)],
                decimal.Parse(coverage, CultureInfo.InvariantCulture)));

    [Fact]
    public void A_score_must_say_what_it_claims_to_measure() =>
        Assert.Throws<DomainValidationException>(
            () => ScoringSpecification.Create(
                MetricId.Create("score.test"),
                CalculationVersion.Create(1, 0),
                [Component(FinancialMetrics.NetMargin)],
                0.5m,
                "   "));

    [Fact]
    public void The_shipped_health_specification_is_coherent()
    {
        var specification = ScoringSpecifications.FinancialHealthV1;

        Assert.Equal("score", specification.Score.Family);
        Assert.Equal(4, specification.Components.Count);
        Assert.Equal(4m, specification.TotalWeight);
        Assert.Equal(0.75m, specification.MinimumCoverage);

        // Every component must be a metric the platform can actually compute, or the score is
        // permanently short of coverage and nobody finds out until it refuses in production.
        var catalogue = FinancialCalculators.All
            .Select(calculator => calculator.Metric.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var component in specification.Components)
        {
            Assert.Contains(component.Metric.Value, catalogue);
        }
    }
}
