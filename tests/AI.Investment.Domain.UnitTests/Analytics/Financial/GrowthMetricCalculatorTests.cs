using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class GrowthMetricCalculatorTests
{
    private static FigureComparison Comparison(decimal current, decimal prior, string attribute) =>
        FigureComparison.Create(
            Financials.Current(Financials.Money(attribute, current)),
            Financials.Prior(Financials.PriorMoney(attribute, prior)));

    [Fact]
    public void Revenue_growth_is_the_change_as_a_proportion_of_the_prior_period()
    {
        var result = FinancialCalculators.RevenueGrowth
            .Calculate(Financials.Context(), Comparison(118.4m, 100m, FinancialFigures.Revenue))
            .RequireResult();

        Assert.Equal(0.184m, result.Value.Amount);
        Assert.Equal(UnitOfMeasure.Ratio, result.Value.Unit);

        // The measurement describes the period that just ended, not the one it is compared with.
        Assert.Equal(Financials.CurrentPeriodEnd, result.AsOfUtc);
        Assert.Equal(2, result.Inputs.Count);
    }

    [Fact]
    public void A_decline_is_negative()
    {
        var result = FinancialCalculators.RevenueGrowth
            .Calculate(Financials.Context(), Comparison(80m, 100m, FinancialFigures.Revenue))
            .RequireResult();

        Assert.Equal(-0.2m, result.Value.Amount);
    }

    /// <summary>
    /// The reason the divisor is an absolute value. A loss narrowing from 100 to 50 is an
    /// improvement, and dividing by a negative prior would report it as -0.5.
    /// </summary>
    [Fact]
    public void A_narrowing_loss_reads_as_improvement()
    {
        var result = FinancialCalculators.EarningsGrowth
            .Calculate(Financials.Context(), Comparison(-50m, -100m, FinancialFigures.NetIncome))
            .RequireResult();

        Assert.Equal(0.5m, result.Value.Amount);
    }

    [Fact]
    public void A_widening_loss_reads_as_deterioration()
    {
        var result = FinancialCalculators.EarningsGrowth
            .Calculate(Financials.Context(), Comparison(-150m, -100m, FinancialFigures.NetIncome))
            .RequireResult();

        Assert.Equal(-0.5m, result.Value.Amount);
    }

    [Fact]
    public void Growth_from_zero_is_undefined()
    {
        var outcome = FinancialCalculators.RevenueGrowth
            .Calculate(Financials.Context(), Comparison(100m, 0m, FinancialFigures.Revenue));

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.UndefinedResult, outcome.Reason);
    }

    /// <summary>Absent history is a different problem from an absent current figure, and says so.</summary>
    [Fact]
    public void A_prior_period_without_the_figure_is_not_enough_history()
    {
        var comparison = FigureComparison.Create(
            Financials.Current(Financials.Money(FinancialFigures.Revenue, 100m)),
            Financials.Prior(Financials.PriorMoney(FinancialFigures.NetIncome, 10m)));

        var outcome = FinancialCalculators.RevenueGrowth.Calculate(Financials.Context(), comparison);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.NotEnoughHistory, outcome.Reason);
    }

    [Fact]
    public void A_current_period_without_the_figure_is_a_missing_input()
    {
        var comparison = FigureComparison.Create(
            Financials.Current(Financials.Money(FinancialFigures.NetIncome, 10m)),
            Financials.Prior(Financials.PriorMoney(FinancialFigures.Revenue, 100m)));

        var outcome = FinancialCalculators.RevenueGrowth.Calculate(Financials.Context(), comparison);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.MissingInput, outcome.Reason);
    }

    [Fact]
    public void A_filing_the_calculation_may_not_yet_see_is_refused()
    {
        var outcome = FinancialCalculators.RevenueGrowth.Calculate(
            Financials.Context(cutoffUtc: Financials.CurrentPublished.AddDays(-1)),
            Comparison(118.4m, 100m, FinancialFigures.Revenue));

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.OutsideKnowledgeCutoff, outcome.Reason);
    }

    /// <summary>Growth is always a proportion, whatever the underlying figure is measured in.</summary>
    [Fact]
    public void Growth_is_always_a_ratio() =>
        Assert.Equal(UnitOfMeasure.Ratio, FinancialCalculators.RevenueGrowth.Unit);
}
