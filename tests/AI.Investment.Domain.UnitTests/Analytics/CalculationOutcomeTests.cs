using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class CalculationOutcomeTests
{
    private static MetricResult Result() =>
        MetricResult.Create(
            AnalyticsEvidence.Context(),
            AnalyticsEvidence.Metric,
            MetricValue.Ratio(0.184m),
            "(revenue - priorRevenue) / |priorRevenue|",
            AnalyticsEvidence.Calculator,
            AnalyticsEvidence.Version,
            AnalyticsEvidence.PeriodEnd,
            [AnalyticsEvidence.Input("revenue", 100m), AnalyticsEvidence.Input("priorRevenue", 84m)]);

    [Fact]
    public void A_computed_outcome_carries_the_measurement_and_no_reason()
    {
        var outcome = CalculationOutcome.Computed(Result());

        Assert.True(outcome.IsComputed);
        Assert.NotNull(outcome.Result);
        Assert.Equal(InsufficientDataReason.None, outcome.Reason);
        Assert.Null(outcome.Explanation);
        Assert.Equal(AnalyticsEvidence.Metric, outcome.Metric);
    }

    [Fact]
    public void A_refusal_names_the_metric_the_reason_and_the_detail()
    {
        var outcome = CalculationOutcome.InsufficientData(
            AnalyticsEvidence.Metric,
            InsufficientDataReason.NotEnoughHistory,
            "Only one reported period is available; growth needs two.");

        Assert.False(outcome.IsComputed);
        Assert.Null(outcome.Result);
        Assert.Equal(InsufficientDataReason.NotEnoughHistory, outcome.Reason);
        Assert.Contains("two", outcome.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>"None" says only that a number is absent, which the caller could already see.</summary>
    [Fact]
    public void A_refusal_must_state_a_reason() =>
        Assert.Throws<DomainValidationException>(
            () => CalculationOutcome.InsufficientData(
                AnalyticsEvidence.Metric,
                InsufficientDataReason.None,
                "nothing to report"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refusal_must_explain_itself(string explanation) =>
        Assert.Throws<DomainValidationException>(
            () => CalculationOutcome.InsufficientData(
                AnalyticsEvidence.Metric,
                InsufficientDataReason.MissingInput,
                explanation));

    /// <summary>
    /// Reading a result off a refusal would invent the number the calculator declined to state, so
    /// it throws rather than returning a default.
    /// </summary>
    [Fact]
    public void Requiring_a_result_from_a_refusal_throws()
    {
        var outcome = CalculationOutcome.InsufficientData(
            AnalyticsEvidence.Metric,
            InsufficientDataReason.UndefinedResult,
            "The prior period's revenue was zero, so growth is undefined.");

        var exception = Assert.Throws<DomainRuleViolationException>(() => _ = outcome.RequireResult());

        Assert.Contains("undefined", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Requiring_a_result_from_a_measurement_returns_it()
    {
        var result = Result();

        Assert.Same(result, CalculationOutcome.Computed(result).RequireResult());
    }

    [Fact]
    public void A_long_explanation_is_truncated_rather_than_refused()
    {
        var outcome = CalculationOutcome.InsufficientData(
            AnalyticsEvidence.Metric,
            InsufficientDataReason.MissingInput,
            new string('a', CalculationOutcome.MaxExplanationLength + 50));

        Assert.Equal(CalculationOutcome.MaxExplanationLength, outcome.Explanation!.Length);
    }
}
