using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class MetricValueTests
{
    [Fact]
    public void A_ratio_and_a_percent_are_different_measurements()
    {
        var ratio = MetricValue.Ratio(0.184m);
        var percent = MetricValue.Percent(18.4m);

        Assert.Equal(UnitOfMeasure.Ratio, ratio.Unit);
        Assert.Equal(UnitOfMeasure.Percent, percent.Unit);

        // The hundred-fold error this separation exists to prevent: the two describe the same
        // growth, and comparing them as though they were the same number is always wrong.
        Assert.False(ratio.IsComparableWith(percent));
        Assert.NotEqual(ratio, percent);
    }

    [Fact]
    public void Money_must_state_its_currency()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => MetricValue.Create(1_000m, UnitOfMeasure.Money));

        Assert.Contains("currency", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anything_that_is_not_money_may_not_carry_a_currency() =>
        Assert.Throws<DomainValidationException>(
            () => MetricValue.Create(0.5m, UnitOfMeasure.Ratio, Currency.Usd));

    [Fact]
    public void A_measurement_with_an_unknown_unit_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => MetricValue.Create(1m, UnitOfMeasure.Unknown));

    [Fact]
    public void Amounts_in_different_currencies_are_not_comparable()
    {
        var usd = MetricValue.Money(1_000m, Currency.Usd);
        var eur = MetricValue.Money(1_000m, Currency.Create("EUR"));

        Assert.False(usd.IsComparableWith(eur));
        Assert.True(usd.IsComparableWith(MetricValue.Money(42m, Currency.Usd)));
    }

    [Fact]
    public void A_value_renders_with_its_unit()
    {
        Assert.Equal("18.4%", MetricValue.Percent(18.4m).ToString());
        Assert.Equal("1000 USD", MetricValue.Money(1_000m, Currency.Usd).ToString());
        Assert.Equal("0.184 (Ratio)", MetricValue.Ratio(0.184m).ToString());
    }

    /// <summary>
    /// Growth beyond the presentation range of <see cref="Percentage"/> is unusual but real, and a
    /// calculation must not throw because a company had a very good quarter.
    /// </summary>
    [Fact]
    public void An_extreme_but_genuine_result_is_accepted()
    {
        var value = MetricValue.Ratio(250m);

        Assert.Equal(250m, value.Amount);
    }
}
