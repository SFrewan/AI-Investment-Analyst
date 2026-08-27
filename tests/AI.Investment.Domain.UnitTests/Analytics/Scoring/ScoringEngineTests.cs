using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Analytics.Scoring;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.UnitTests.Analytics.Financial;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Scoring;

public sealed class ScoringEngineTests
{
    private static readonly ScoringEngine Engine = ScoringSpecifications.FinancialHealthEngine;

    /// <summary>
    /// A measurement supplied straight to the engine, so these tests exercise scoring rather than
    /// the financial formulas that normally produce the components.
    /// </summary>
    private static MetricResult Measurement(
        CalculationContext context,
        MetricId metric,
        decimal amount,
        DateTime? asOfUtc = null,
        DateTime? publishedUtc = null)
    {
        var asOf = asOfUtc ?? Financials.CurrentPeriodEnd;

        return MetricResult.Create(
            context,
            metric,
            MetricValue.Ratio(amount),
            "supplied directly by the test",
            SourceId.Create("calc.test.measurement"),
            CalculationVersion.Create(1, 0),
            asOf,
            [
                CalculationInput.Create(
                    "input",
                    Financials.Fact(amount, asOf, publishedUtc ?? Financials.CurrentPublished),
                    UnitOfMeasure.Ratio),
            ]);
    }

    /// <summary>The four components at values chosen so the arithmetic is exact.</summary>
    private static List<MetricResult> HealthyEnough(CalculationContext context) =>
    [
        Measurement(context, FinancialMetrics.NetMargin, 0.1m),          // 0.1 / 0.25 => 0.4
        Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),       // (2-1) / (3-1) => 0.5
        Measurement(context, FinancialMetrics.DebtToEquity, 1.0m),       // (1-2) / (0-2) => 0.5
        Measurement(context, FinancialMetrics.FreeCashFlowMargin, 0.1m), // 0.1 / 0.20 => 0.5
    ];

    [Fact]
    public void A_score_is_the_declared_weighted_average_of_its_components()
    {
        var context = Financials.Context();

        var result = Engine.Calculate(context, HealthyEnough(context)).RequireResult();

        Assert.Equal(0.475m, result.Value.Amount);
        Assert.Equal(UnitOfMeasure.Ratio, result.Value.Unit);
        Assert.Equal(ScoringSpecifications.FinancialHealth, result.Metric);
        Assert.Equal(ScoringSpecifications.FinancialHealthV1.Version, result.Version);
        Assert.Equal(4, result.Inputs.Count);
        Assert.Empty(result.Caveats);
    }

    /// <summary>
    /// One absent line item must not destroy a score four other measurements support - but the
    /// score has to say it was computed from less than the whole specification.
    /// </summary>
    [Fact]
    public void A_missing_component_still_scores_when_coverage_holds_and_says_so()
    {
        var context = Financials.Context();

        var partial = new List<MetricResult>
        {
            Measurement(context, FinancialMetrics.NetMargin, 0.125m),
            Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),
            Measurement(context, FinancialMetrics.DebtToEquity, 1.0m),
        };

        var result = Engine.Calculate(context, partial).RequireResult();

        Assert.Equal(0.5m, result.Value.Amount);
        Assert.Equal(3, result.Inputs.Count);

        var caveat = Assert.Single(result.Caveats);
        Assert.Contains(FinancialMetrics.FreeCashFlowMargin.Value, caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_below_the_declared_minimum_refuses_and_names_what_is_missing()
    {
        var context = Financials.Context();

        var tooLittle = new List<MetricResult>
        {
            Measurement(context, FinancialMetrics.NetMargin, 0.1m),
            Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),
        };

        var outcome = Engine.Calculate(context, tooLittle);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.MissingInput, outcome.Reason);
        Assert.Contains(FinancialMetrics.DebtToEquity.Value, outcome.Explanation!, StringComparison.Ordinal);
        Assert.Contains(FinancialMetrics.FreeCashFlowMargin.Value, outcome.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>Choosing between two values for one metric is not a decision arithmetic may make.</summary>
    [Fact]
    public void Two_measurements_of_one_metric_are_conflicting_evidence()
    {
        var context = Financials.Context();

        var conflicting = HealthyEnough(context);
        conflicting.Add(Measurement(context, FinancialMetrics.NetMargin, 0.2m));

        var outcome = Engine.Calculate(context, conflicting);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.ConflictingEvidence, outcome.Reason);
    }

    [Fact]
    public void A_score_is_a_calculation_that_cites_every_component()
    {
        var context = Financials.Context();

        var result = Engine.Calculate(context, HealthyEnough(context)).RequireResult();
        var claim = result.ToClaim();

        Assert.Equal(ClaimKind.Calculation, claim.Kind);
        Assert.Equal(4, claim.DerivedFrom.Count);
        Assert.Equal(0.475m, claim.Value);

        // Each component is itself a calculation, so the chain continues back to the filings.
        foreach (var input in result.Inputs)
        {
            Assert.Equal(ClaimKind.Calculation, input.Evidence.Kind);
            Assert.NotEmpty(input.Evidence.DerivedFrom);
        }
    }

    /// <summary>
    /// The reason normalisation clamps. A 500% net margin is at the top of the scale and no
    /// further; without the clamp it would drag the whole score with it.
    /// </summary>
    [Fact]
    public void An_extraordinary_component_cannot_dominate_the_score()
    {
        var context = Financials.Context();

        var extreme = new List<MetricResult>
        {
            Measurement(context, FinancialMetrics.NetMargin, 5.0m),
            Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),
            Measurement(context, FinancialMetrics.DebtToEquity, 1.0m),
            Measurement(context, FinancialMetrics.FreeCashFlowMargin, 0.1m),
        };

        var result = Engine.Calculate(context, extreme).RequireResult();

        Assert.Equal(0.625m, result.Value.Amount);
    }

    [Fact]
    public void Leverage_scores_higher_when_it_is_lower()
    {
        var context = Financials.Context();

        decimal ScoreWithLeverage(decimal debtToEquity) => Engine.Calculate(
            context,
            new List<MetricResult>
            {
                Measurement(context, FinancialMetrics.NetMargin, 0.1m),
                Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),
                Measurement(context, FinancialMetrics.DebtToEquity, debtToEquity),
                Measurement(context, FinancialMetrics.FreeCashFlowMargin, 0.1m),
            }).RequireResult().Value.Amount;

        Assert.True(ScoreWithLeverage(0m) > ScoreWithLeverage(2m));
    }

    /// <summary>A score that silently mixes periods is one nobody can date.</summary>
    [Fact]
    public void A_component_describing_an_earlier_period_is_named_in_a_caveat()
    {
        var context = Financials.Context();

        var mixed = new List<MetricResult>
        {
            Measurement(context, FinancialMetrics.NetMargin, 0.1m),
            Measurement(context, FinancialMetrics.CurrentRatio, 2.0m),
            Measurement(context, FinancialMetrics.DebtToEquity, 1.0m),
            Measurement(
                context,
                FinancialMetrics.FreeCashFlowMargin,
                0.1m,
                asOfUtc: Financials.PriorPeriodEnd,
                publishedUtc: Financials.PriorPublished),
        };

        var result = Engine.Calculate(context, mixed).RequireResult();

        Assert.Equal(Financials.CurrentPeriodEnd, result.AsOfUtc);

        var caveat = Assert.Single(result.Caveats);
        Assert.Contains(FinancialMetrics.FreeCashFlowMargin.Value, caveat, StringComparison.Ordinal);
        Assert.Contains("2024-12-31", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_component_published_after_the_cutoff_is_refused()
    {
        var permitted = Financials.Context();
        var components = HealthyEnough(permitted);

        var replay = Financials.Context(cutoffUtc: Financials.CurrentPublished.AddDays(-1));

        var outcome = Engine.Calculate(replay, components);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.OutsideKnowledgeCutoff, outcome.Reason);
    }

    [Fact]
    public void Measurements_the_specification_does_not_name_are_ignored()
    {
        var context = Financials.Context();

        var withExtras = HealthyEnough(context);
        withExtras.Add(Measurement(context, FinancialMetrics.GrossMargin, 0.44m));

        var result = Engine.Calculate(context, withExtras).RequireResult();

        Assert.Equal(0.475m, result.Value.Amount);
        Assert.Equal(4, result.Inputs.Count);
        Assert.DoesNotContain(result.Inputs, input => input.Name == FinancialMetrics.GrossMargin.Value);
    }
}
