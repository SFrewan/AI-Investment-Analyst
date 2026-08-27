using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class MetricResultTests
{
    private const string Formula = "(revenue - priorRevenue) / |priorRevenue|";

    private static MetricResult Create(
        CalculationContext? context = null,
        DateTime? asOfUtc = null,
        IEnumerable<CalculationInput>? inputs = null,
        IEnumerable<string>? caveats = null) =>
        MetricResult.Create(
            context ?? AnalyticsEvidence.Context(),
            AnalyticsEvidence.Metric,
            MetricValue.Ratio(0.1904m),
            Formula,
            AnalyticsEvidence.Calculator,
            AnalyticsEvidence.Version,
            asOfUtc ?? AnalyticsEvidence.PeriodEnd,
            inputs ?? new[]
            {
                AnalyticsEvidence.Input("revenue", 100m),
                AnalyticsEvidence.Input("priorRevenue", 84m),
            },
            caveats);

    [Fact]
    public void A_measurement_preserves_everything_needed_to_explain_it()
    {
        var result = Create();

        Assert.Equal(AnalyticsEvidence.Metric, result.Metric);
        Assert.Equal(AnalyticsEvidence.Subject, result.Subject);
        Assert.Equal(MetricValue.Ratio(0.1904m), result.Value);
        Assert.Equal(Formula, result.Formula);
        Assert.Equal(AnalyticsEvidence.Calculator, result.CalculatorId);
        Assert.Equal(AnalyticsEvidence.Version, result.Version);
        Assert.Equal(AnalyticsEvidence.PeriodEnd, result.AsOfUtc);
        Assert.Equal(AnalyticsEvidence.Now, result.CalculatedAtUtc);
        Assert.Equal(AnalyticsEvidence.Now, result.Cutoff.AsOfUtc);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Empty(result.Caveats);
    }

    /// <summary>
    /// The period a measurement describes and the moment it was taken are different facts, and a
    /// backtest depends on their staying different.
    /// </summary>
    [Fact]
    public void The_period_described_and_the_moment_measured_are_recorded_separately()
    {
        var result = Create();

        Assert.NotEqual(result.AsOfUtc, result.CalculatedAtUtc);
        Assert.True(result.AsOfUtc < result.CalculatedAtUtc);
    }

    [Fact]
    public void A_measurement_must_record_its_inputs()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Create(inputs: Array.Empty<CalculationInput>()));

        Assert.Contains("inputs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_formula_is_required() =>
        Assert.Throws<DomainValidationException>(
            () => MetricResult.Create(
                AnalyticsEvidence.Context(),
                AnalyticsEvidence.Metric,
                MetricValue.Ratio(0.1m),
                "   ",
                AnalyticsEvidence.Calculator,
                AnalyticsEvidence.Version,
                AnalyticsEvidence.PeriodEnd,
                [AnalyticsEvidence.Input("revenue", 100m)]));

    /// <summary>Two inputs under one name make it impossible to say which the formula used.</summary>
    [Fact]
    public void Each_term_may_appear_only_once()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Create(inputs:
            [
                AnalyticsEvidence.Input("revenue", 100m),
                AnalyticsEvidence.Input("revenue", 84m),
            ]));

        Assert.Contains("revenue", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect this whole design exists to prevent: a measurement built on a filing that had not
    /// been published yet at the cutoff it claims to respect.
    /// </summary>
    [Fact]
    public void An_input_published_after_the_cutoff_is_refused()
    {
        var context = AnalyticsEvidence.Context(cutoffUtc: AnalyticsEvidence.Now);

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Create(
                context: context,
                inputs:
                [
                    AnalyticsEvidence.Input("revenue", 100m),
                    AnalyticsEvidence.Input("priorRevenue", 84m, AnalyticsEvidence.Now.AddDays(1)),
                ]));

        Assert.Contains("priorRevenue", exception.Message, StringComparison.Ordinal);
        Assert.Contains("knowledge cutoff", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backtest that could see the filing is fine; the same filing under an earlier cutoff is not.
    /// The evidence did not change - only what the calculation was permitted to know.
    /// </summary>
    [Fact]
    public void The_same_evidence_is_admitted_or_refused_by_the_cutoff_alone()
    {
        var input = AnalyticsEvidence.Input("revenue", 100m);

        var permitted = AnalyticsEvidence.Context(cutoffUtc: AnalyticsEvidence.Now);
        var earlier = AnalyticsEvidence.Context(
            cutoffUtc: AnalyticsEvidence.Published.AddDays(-1),
            calculatedAtUtc: AnalyticsEvidence.Now);

        var admitted = Create(context: permitted, inputs: [input]);
        Assert.Equal(AnalyticsEvidence.Metric, admitted.Metric);

        Assert.Throws<DomainRuleViolationException>(
            () => Create(context: earlier, asOfUtc: AnalyticsEvidence.PeriodEnd, inputs: [input]));
    }

    [Fact]
    public void A_measurement_may_not_describe_a_period_beyond_the_cutoff()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Create(asOfUtc: AnalyticsEvidence.Now.AddDays(1)));

        Assert.Contains("cutoff", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_period_must_be_utc() =>
        Assert.Throws<DomainValidationException>(
            () => Create(asOfUtc: new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void Caveats_are_normalised_and_blank_ones_dropped()
    {
        var result = Create(caveats: ["  restated  ", "", "   ", "unaudited"]);

        Assert.Equal(2, result.Caveats.Count);
        Assert.Equal("restated", result.Caveats[0]);
        Assert.Equal("unaudited", result.Caveats[1]);
    }

    /// <summary>
    /// A measurement enters the epistemic model as a Calculation, never a Fact, and names the
    /// claims underneath it so a reader can walk back to the filings.
    /// </summary>
    [Fact]
    public void A_measurement_becomes_a_calculation_claim_that_cites_its_evidence()
    {
        var revenue = AnalyticsEvidence.Input("revenue", 100m);
        var prior = AnalyticsEvidence.Input("priorRevenue", 84m);

        var claim = Create(inputs: [revenue, prior]).ToClaim();

        Assert.Equal(ClaimKind.Calculation, claim.Kind);
        Assert.False(claim.IsFact);
        Assert.Equal(0.1904m, claim.Value);
        Assert.Null(claim.Confidence);
        Assert.Equal(2, claim.DerivedFrom.Count);
        Assert.Contains(revenue.EvidenceId, claim.DerivedFrom);
        Assert.Contains(prior.EvidenceId, claim.DerivedFrom);
        Assert.Equal(AnalyticsEvidence.Calculator, claim.Provenance.SourceId);
        Assert.Equal(AnalyticsEvidence.PeriodEnd, claim.Provenance.AsOfUtc);
        // Knowable when its slowest input was, not when the arithmetic happened - which is what
        // lets a derived figure be used as evidence in a replay of the past.
        Assert.Equal(AnalyticsEvidence.Published, claim.Provenance.PublishedAtUtc);
        Assert.Equal(AnalyticsEvidence.Now, claim.Provenance.RetrievedAtUtc);
    }

    [Fact]
    public void The_claim_carries_the_measurement_caveats()
    {
        var claim = Create(caveats: ["unaudited"]).ToClaim();

        Assert.Single(claim.Caveats);
        Assert.Equal("unaudited", claim.Caveats[0]);
    }
}
