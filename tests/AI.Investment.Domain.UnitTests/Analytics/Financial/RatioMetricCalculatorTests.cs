using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class RatioMetricCalculatorTests
{
    [Fact]
    public void A_margin_is_computed_and_explains_itself()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.GrossProfit, 44_000m),
            Financials.Money(FinancialFigures.Revenue, 100_000m));

        var outcome = FinancialCalculators.GrossMargin.Calculate(Financials.Context(), figures);

        var result = outcome.RequireResult();

        Assert.Equal(0.44m, result.Value.Amount);
        Assert.Equal(UnitOfMeasure.Ratio, result.Value.Unit);
        Assert.Equal(FinancialMetrics.GrossMargin, result.Metric);
        Assert.Equal(Financials.CurrentPeriodEnd, result.AsOfUtc);
        Assert.Equal(FinancialCalculators.Version1, result.Version);
        Assert.Equal(SourceId.Create("calc.financial.gross-margin"), result.CalculatorId);

        // The stored inputs name the line items they came from, not "numerator" and "denominator",
        // so a reader can check the figure against the filing without consulting the code.
        Assert.Equal(2, result.Inputs.Count);
        Assert.Contains(result.Inputs, input => input.Name == FinancialFigures.GrossProfit);
        Assert.Contains(result.Inputs, input => input.Name == FinancialFigures.Revenue);
        Assert.Contains(FinancialFigures.Revenue, result.Formula, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_line_item_is_a_stated_refusal_rather_than_a_zero()
    {
        var figures = Financials.Current(Financials.Money(FinancialFigures.Revenue, 100_000m));

        var outcome = FinancialCalculators.GrossMargin.Calculate(Financials.Context(), figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.MissingInput, outcome.Reason);
        Assert.Contains(FinancialFigures.GrossProfit, outcome.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_denominator_is_undefined_rather_than_infinite()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.GrossProfit, 44_000m),
            Financials.Money(FinancialFigures.Revenue, 0m));

        var outcome = FinancialCalculators.GrossMargin.Calculate(Financials.Context(), figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.UndefinedResult, outcome.Reason);
    }

    [Fact]
    public void Earnings_per_share_is_money_divided_by_a_count_and_keeps_the_currency()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.NetIncome, 1_000m),
            Financials.Shares(FinancialFigures.DilutedShares, 250m));

        var result = FinancialCalculators.EarningsPerShareDiluted
            .Calculate(Financials.Context(), figures)
            .RequireResult();

        Assert.Equal(4m, result.Value.Amount);
        Assert.Equal(UnitOfMeasure.Money, result.Value.Unit);
        Assert.Equal(Currency.Usd, result.Value.Currency);
    }

    /// <summary>Money over a share count is not dimensionless, whatever the arithmetic says.</summary>
    [Fact]
    public void A_dimensionless_ratio_refuses_mismatched_units()
    {
        var calculator = new RatioMetricCalculator(
            MetricId.Create("test.money-over-count"),
            SourceId.Create("calc.test.money-over-count"),
            FinancialCalculators.Version1,
            UnitOfMeasure.Ratio,
            FinancialFigures.NetIncome,
            FinancialFigures.DilutedShares,
            "netIncome / dilutedShares");

        var figures = Financials.Current(
            Financials.Money(FinancialFigures.NetIncome, 1_000m),
            Financials.Shares(FinancialFigures.DilutedShares, 250m));

        var outcome = calculator.Calculate(Financials.Context(), figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.UnitMismatch, outcome.Reason);
    }

    /// <summary>
    /// Evidence the calculation was not yet allowed to see is an ordinary refusal, not an
    /// exception - meeting it is normal when replaying history.
    /// </summary>
    [Fact]
    public void Evidence_published_after_the_cutoff_is_refused_without_throwing()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.GrossProfit, 44_000m),
            Financials.Money(FinancialFigures.Revenue, 100_000m));

        var beforeTheFiling = Financials.Context(cutoffUtc: Financials.CurrentPublished.AddDays(-1));

        var outcome = FinancialCalculators.GrossMargin.Calculate(beforeTheFiling, figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.OutsideKnowledgeCutoff, outcome.Reason);
    }

    /// <summary>Mismatched subjects are a wiring mistake, not a gap in the data.</summary>
    [Fact]
    public void Figures_belonging_to_another_subject_throw()
    {
        var other = ReportedFigures.Create(
            IngestionSubject.Create("company", "MSFT"),
            Financials.CurrentPeriodEnd,
            Currency.Usd,
            [
                Financials.Money(FinancialFigures.GrossProfit, 1m),
                Financials.Money(FinancialFigures.Revenue, 2m),
            ]);

        Assert.Throws<DomainRuleViolationException>(
            () => FinancialCalculators.GrossMargin.Calculate(Financials.Context(), other));
    }

    [Fact]
    public void A_figure_divided_by_itself_is_refused_at_construction() =>
        Assert.Throws<DomainValidationException>(
            () => new RatioMetricCalculator(
                MetricId.Create("test.self"),
                SourceId.Create("calc.test.self"),
                FinancialCalculators.Version1,
                UnitOfMeasure.Ratio,
                FinancialFigures.Revenue,
                FinancialFigures.Revenue,
                "revenue / revenue"));

    [Theory]
    [InlineData(UnitOfMeasure.Count)]
    [InlineData(UnitOfMeasure.Days)]
    [InlineData(UnitOfMeasure.Percent)]
    [InlineData(UnitOfMeasure.Unknown)]
    public void A_division_may_only_produce_a_ratio_or_money(UnitOfMeasure unit) =>
        Assert.Throws<DomainValidationException>(
            () => new RatioMetricCalculator(
                MetricId.Create("test.bad-unit"),
                SourceId.Create("calc.test.bad-unit"),
                FinancialCalculators.Version1,
                unit,
                FinancialFigures.NetIncome,
                FinancialFigures.Revenue,
                "netIncome / revenue"));
}
