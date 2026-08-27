using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

public sealed class ReportedFiguresTests
{
    [Fact]
    public void A_figure_is_found_however_the_attribute_was_cased()
    {
        var figures = Financials.Current(Financials.Money("Financials.Revenue", 100m));

        Assert.True(figures.TryFind(FinancialFigures.Revenue, out var found));
        Assert.Equal(100m, found.Value);
        Assert.Equal(FinancialFigures.Revenue, found.Attribute);
    }

    [Fact]
    public void A_missing_figure_is_reported_as_absent_rather_than_as_zero()
    {
        var figures = Financials.Current(Financials.Money(FinancialFigures.Revenue, 100m));

        Assert.False(figures.TryFind(FinancialFigures.NetIncome, out var found));
        Assert.Null(found);
    }

    /// <summary>
    /// Which of two values for one line item is right is a question about the data. Analytics must
    /// not answer it by keeping whichever arrived last.
    /// </summary>
    [Fact]
    public void One_line_item_may_not_be_reported_twice()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Financials.Current(
                Financials.Money(FinancialFigures.Revenue, 100m),
                Financials.Money(FinancialFigures.Revenue, 101m)));

        Assert.Contains(FinancialFigures.Revenue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_a_computed_figure_leaves_the_original_period_untouched()
    {
        var reported = Financials.Current(Financials.Money(FinancialFigures.Revenue, 100m));

        var enriched = reported.With(Financials.Money(FinancialFigures.FreeCashFlow, 20m));

        Assert.True(enriched.TryFind(FinancialFigures.FreeCashFlow, out _));
        Assert.False(reported.TryFind(FinancialFigures.FreeCashFlow, out _));
        Assert.Equal(2, enriched.Figures.Count);
        Assert.Single(reported.Figures);
    }

    [Fact]
    public void A_period_end_must_be_utc() =>
        Assert.Throws<DomainValidationException>(
            () => ReportedFigures.Create(
                Financials.Subject,
                new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Local),
                Currency.Usd,
                [Financials.Money(FinancialFigures.Revenue, 1m)]));

    [Fact]
    public void A_figure_may_not_be_null() =>
        Assert.Throws<DomainValidationException>(
            () => ReportedFigures.Create(
                Financials.Subject,
                Financials.CurrentPeriodEnd,
                Currency.Usd,
                [null!]));

    [Fact]
    public void A_subject_is_required() =>
        Assert.Throws<ArgumentNullException>(
            () => ReportedFigures.Create(
                null!,
                Financials.CurrentPeriodEnd,
                Currency.Usd,
                Array.Empty<ReportedFigure>()));

    [Fact]
    public void A_figure_must_carry_a_usable_unit() =>
        Assert.Throws<DomainValidationException>(
            () => ReportedFigure.Create(
                FinancialFigures.Revenue,
                Financials.Fact(1m, Financials.CurrentPeriodEnd, Financials.CurrentPublished),
                UnitOfMeasure.Unknown));

    [Fact]
    public void An_unnamed_figure_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ReportedFigure.OfMoney(
                "  ",
                Financials.Fact(1m, Financials.CurrentPeriodEnd, Financials.CurrentPublished)));
}
