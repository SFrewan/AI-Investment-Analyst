using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class FinancialCatalogueTests
{
    [Fact]
    public void Every_calculator_measures_something_different()
    {
        var metrics = FinancialCalculators.All.Select(calculator => calculator.Metric.Value).ToList();

        Assert.Equal(metrics.Count, metrics.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A stored result's provenance names its producer, so the producer must be identifiable and
    /// must not be shared between two different formulas.
    /// </summary>
    [Fact]
    public void Every_calculator_has_its_own_identity_derived_from_what_it_measures()
    {
        var producers = FinancialCalculators.All.Select(calculator => calculator.CalculatorId.Value).ToList();

        Assert.Equal(producers.Count, producers.Distinct(StringComparer.Ordinal).Count());

        foreach (var calculator in FinancialCalculators.All)
        {
            Assert.Equal($"calc.{calculator.Metric.Value}", calculator.CalculatorId.Value);
        }
    }

    [Fact]
    public void Every_calculator_states_a_version_and_a_usable_unit()
    {
        foreach (var calculator in FinancialCalculators.All)
        {
            Assert.NotNull(calculator.Version);
            Assert.NotEqual(UnitOfMeasure.Unknown, calculator.Unit);
        }
    }

    [Fact]
    public void Every_metric_belongs_to_the_financial_family()
    {
        foreach (var calculator in FinancialCalculators.All)
        {
            Assert.Equal("financial", calculator.Metric.Family);
        }
    }

    /// <summary>
    /// The chaining the catalogue is built around: free cash flow is computed, added to the period
    /// as a figure, and then divided by revenue. The margin's evidence therefore runs back through
    /// the free cash flow calculation to the two filings underneath it.
    /// </summary>
    [Fact]
    public void A_calculation_can_stand_on_another_calculation()
    {
        var context = Financials.Context();

        var reported = Financials.Current(
            Financials.Money(FinancialFigures.OperatingCashFlow, 1_000m),
            Financials.Money(FinancialFigures.CapitalExpenditure, 300m),
            Financials.Money(FinancialFigures.Revenue, 3_500m));

        var freeCashFlow = FinancialCalculators.FreeCashFlow.Calculate(context, reported).RequireResult();

        var enriched = reported.With(
            ReportedFigure.OfMoney(FinancialFigures.FreeCashFlow, freeCashFlow.ToClaim()));

        var margin = FinancialCalculators.FreeCashFlowMargin.Calculate(context, enriched).RequireResult();

        Assert.Equal(0.2m, margin.Value.Amount);

        var derived = margin.Inputs.Single(input => input.Name == FinancialFigures.FreeCashFlow);

        Assert.Equal(ClaimKind.Calculation, derived.Evidence.Kind);
        Assert.Equal(2, derived.Evidence.DerivedFrom.Count);
    }

    /// <summary>
    /// The same chain under a cutoff between the filing and today. A derived figure is knowable when
    /// its slowest input was, so it survives here - if it were stamped with the wall-clock time the
    /// arithmetic ran, nothing derived could ever be backtested.
    /// </summary>
    [Fact]
    public void A_derived_figure_remains_usable_when_replaying_the_past()
    {
        var replay = Financials.Context(cutoffUtc: Financials.CurrentPublished);

        var reported = Financials.Current(
            Financials.Money(FinancialFigures.OperatingCashFlow, 1_000m),
            Financials.Money(FinancialFigures.CapitalExpenditure, 300m),
            Financials.Money(FinancialFigures.Revenue, 3_500m));

        var freeCashFlow = FinancialCalculators.FreeCashFlow.Calculate(replay, reported).RequireResult();

        Assert.Equal(Financials.CurrentPublished, freeCashFlow.EvidenceAvailableAtUtc);

        var enriched = reported.With(
            ReportedFigure.OfMoney(FinancialFigures.FreeCashFlow, freeCashFlow.ToClaim()));

        var outcome = FinancialCalculators.FreeCashFlowMargin.Calculate(replay, enriched);

        Assert.True(outcome.IsComputed);
    }

    [Fact]
    public void The_catalogue_covers_the_foundational_financial_measures() =>
        Assert.Equal(22, FinancialCalculators.All.Count);
}
