using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class FigureComparisonTests
{
    [Fact]
    public void Two_ordered_periods_of_one_subject_compare()
    {
        var comparison = FigureComparison.Create(
            Financials.Current(Financials.Money(FinancialFigures.Revenue, 118.4m)),
            Financials.Prior(Financials.PriorMoney(FinancialFigures.Revenue, 100m)));

        Assert.Equal(Financials.CurrentPeriodEnd, comparison.Current.PeriodEndUtc);
        Assert.Equal(Financials.PriorPeriodEnd, comparison.Prior.PeriodEndUtc);
    }

    [Fact]
    public void A_comparison_must_be_about_one_subject()
    {
        var other = ReportedFigures.Create(
            IngestionSubject.Create("company", "MSFT"),
            Financials.PriorPeriodEnd,
            Currency.Usd,
            [Financials.PriorMoney(FinancialFigures.Revenue, 100m)]);

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => FigureComparison.Create(
                Financials.Current(Financials.Money(FinancialFigures.Revenue, 118m)),
                other));

        Assert.Contains("one subject", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Comparing across currencies without conversion measures the exchange rate.</summary>
    [Fact]
    public void A_comparison_must_be_in_one_currency()
    {
        var euros = ReportedFigures.Create(
            Financials.Subject,
            Financials.PriorPeriodEnd,
            Currency.Create("EUR"),
            [Financials.PriorMoney(FinancialFigures.Revenue, 100m)]);

        Assert.Throws<DomainRuleViolationException>(
            () => FigureComparison.Create(
                Financials.Current(Financials.Money(FinancialFigures.Revenue, 118m)),
                euros));
    }

    /// <summary>Reversed, every growth figure changes sign - and still looks like a measurement.</summary>
    [Fact]
    public void The_prior_period_must_end_first()
    {
        var current = Financials.Current(Financials.Money(FinancialFigures.Revenue, 118m));
        var prior = Financials.Prior(Financials.PriorMoney(FinancialFigures.Revenue, 100m));

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => FigureComparison.Create(prior, current));

        Assert.Contains("must end before", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_periods_ending_at_the_same_instant_are_not_a_comparison()
    {
        var current = Financials.Current(Financials.Money(FinancialFigures.Revenue, 118m));

        Assert.Throws<DomainRuleViolationException>(() => FigureComparison.Create(current, current));
    }
}
