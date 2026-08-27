using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class SumMetricCalculatorTests
{
    [Fact]
    public void Free_cash_flow_is_operating_cash_flow_less_capital_expenditure()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.OperatingCashFlow, 1_000m),
            Financials.Money(FinancialFigures.CapitalExpenditure, 300m));

        var result = FinancialCalculators.FreeCashFlow
            .Calculate(Financials.Context(), figures)
            .RequireResult();

        Assert.Equal(700m, result.Value.Amount);
        Assert.Equal(UnitOfMeasure.Money, result.Value.Unit);
        Assert.Equal(Currency.Usd, result.Value.Currency);
        Assert.Equal(2, result.Inputs.Count);
    }

    [Fact]
    public void Ebitda_adds_back_depreciation_and_amortisation()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.OperatingIncome, 500m),
            Financials.Money(FinancialFigures.DepreciationAndAmortisation, 120m));

        var result = FinancialCalculators.Ebitda
            .Calculate(Financials.Context(), figures)
            .RequireResult();

        Assert.Equal(620m, result.Value.Amount);
    }

    [Fact]
    public void Net_debt_is_debt_less_cash()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.TotalDebt, 900m),
            Financials.Money(FinancialFigures.CashAndEquivalents, 250m));

        var result = FinancialCalculators.NetDebt
            .Calculate(Financials.Context(), figures)
            .RequireResult();

        Assert.Equal(650m, result.Value.Amount);
    }

    /// <summary>A company holding more cash than debt has negative net debt, which is a real state.</summary>
    [Fact]
    public void Net_debt_may_be_negative()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.TotalDebt, 100m),
            Financials.Money(FinancialFigures.CashAndEquivalents, 400m));

        var result = FinancialCalculators.NetDebt
            .Calculate(Financials.Context(), figures)
            .RequireResult();

        Assert.Equal(-300m, result.Value.Amount);
    }

    [Fact]
    public void A_missing_term_is_a_stated_refusal()
    {
        var figures = Financials.Current(Financials.Money(FinancialFigures.OperatingCashFlow, 1_000m));

        var outcome = FinancialCalculators.FreeCashFlow.Calculate(Financials.Context(), figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.MissingInput, outcome.Reason);
        Assert.Contains(FinancialFigures.CapitalExpenditure, outcome.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>Adding money to a share count produces a number, and that is the danger.</summary>
    [Fact]
    public void A_term_in_the_wrong_unit_is_refused()
    {
        var figures = Financials.Current(
            Financials.Money(FinancialFigures.OperatingCashFlow, 1_000m),
            Financials.Shares(FinancialFigures.CapitalExpenditure, 300m));

        var outcome = FinancialCalculators.FreeCashFlow.Calculate(Financials.Context(), figures);

        Assert.False(outcome.IsComputed);
        Assert.Equal(InsufficientDataReason.UnitMismatch, outcome.Reason);
    }

    [Fact]
    public void A_sum_of_one_term_is_a_rename_and_is_refused() =>
        Assert.Throws<DomainValidationException>(() => Sum([SumTerm.Plus(FinancialFigures.Revenue)]));

    [Fact]
    public void A_figure_may_not_appear_twice_in_a_sum() =>
        Assert.Throws<DomainValidationException>(() => Sum(
        [
            SumTerm.Plus(FinancialFigures.Revenue),
            SumTerm.Minus(FinancialFigures.Revenue),
        ]));

    [Fact]
    public void A_coefficient_of_zero_is_refused() =>
        Assert.Throws<DomainValidationException>(() => SumTerm.Create(FinancialFigures.Revenue, 0m));

    [Fact]
    public void Only_quantities_of_one_kind_may_be_added() =>
        Assert.Throws<DomainValidationException>(
            () => new SumMetricCalculator(
                MetricId.Create("test.bad-sum"),
                SourceId.Create("calc.test.bad-sum"),
                FinancialCalculators.Version1,
                UnitOfMeasure.Ratio,
                [
                    SumTerm.Plus(FinancialFigures.Revenue),
                    SumTerm.Plus(FinancialFigures.NetIncome),
                ],
                "revenue + netIncome"));

    private static SumMetricCalculator Sum(IEnumerable<SumTerm> terms) =>
        new(
            MetricId.Create("test.sum"),
            SourceId.Create("calc.test.sum"),
            FinancialCalculators.Version1,
            UnitOfMeasure.Money,
            terms,
            "a sum");
}
