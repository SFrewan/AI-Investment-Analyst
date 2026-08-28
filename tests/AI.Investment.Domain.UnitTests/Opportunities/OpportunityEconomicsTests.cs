using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The economics block: every figure in it is calculated, and none of them can be stated.
/// </summary>
/// <remarks>
/// There is deliberately no constructor parameter for profit, margin or risk-adjusted return. The
/// tests below assert the arithmetic and the refusals; that the type has no way to accept those
/// three as inputs is asserted by the compiler, which is the stronger place for it.
/// </remarks>
public sealed class OpportunityEconomicsTests
{
    private static readonly DateTime Start = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

    private static DateRange Horizon => DateRange.Create(Start, Start.AddDays(30));

    [Fact]
    public void Profit_is_revenue_less_cost()
    {
        var economics = Create(cost: 400m, revenue: 1000m);

        Assert.Equal(600m, economics.EstimatedProfit.Amount);
    }

    [Fact]
    public void Margin_is_profit_over_revenue()
    {
        var economics = Create(cost: 400m, revenue: 1000m);

        Assert.Equal(0.6m, economics.Margin.Ratio);
    }

    [Fact]
    public void Margin_is_zero_when_there_is_no_revenue_rather_than_a_division_by_zero()
    {
        var economics = Create(cost: 0m, revenue: 0m, capital: 0m);

        Assert.Equal(Percentage.Zero, economics.Margin);
        Assert.True(economics.HasNoFinancialEffect);
    }

    [Fact]
    public void The_risk_adjusted_return_weights_profit_by_the_stated_probability()
    {
        var economics = Create(cost: 400m, revenue: 1000m, probability: 0.25m);

        Assert.Equal(150m, economics.RiskAdjustedReturn.Amount);
    }

    [Fact]
    public void A_loss_making_estimate_is_recorded_rather_than_rounded_up()
    {
        var economics = Create(cost: 1000m, revenue: 400m);

        Assert.Equal(-600m, economics.EstimatedProfit.Amount);
        Assert.Equal(-1.5m, economics.Margin.Ratio);
    }

    [Theory]
    [InlineData(-1)]
    public void A_negative_cost_is_refused(int cost)
    {
        Assert.Throws<DomainValidationException>(() => Create(cost: cost, revenue: 10m));
    }

    [Fact]
    public void A_negative_revenue_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => Create(cost: 10m, revenue: -1m));
    }

    [Fact]
    public void A_negative_required_capital_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => Create(capital: -1m));
    }

    [Fact]
    public void Every_amount_must_share_a_currency()
    {
        Assert.Throws<DomainValidationException>(() =>
            OpportunityEconomics.Create(
                Money.Create(100m, Currency.Usd),
                Money.Create(120m, Currency.Create("EUR")),
                Money.Create(100m, Currency.Usd),
                Percentage.FromRatio(0.5m),
                Horizon));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_probability_outside_zero_to_one_is_refused(double probability)
    {
        Assert.Throws<DomainValidationException>(() =>
            Create(probability: (decimal)probability));
    }

    [Fact]
    public void A_margin_no_percentage_can_represent_is_a_unit_error_and_is_refused()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            Create(cost: 1_000_000m, revenue: 1m));

        Assert.Equal("OpportunityEconomics.ImplausibleMargin", error.Rule);
    }

    [Fact]
    public void A_no_effect_block_still_carries_a_horizon()
    {
        var economics = OpportunityEconomics.NoFinancialEffect(Currency.Usd, Horizon);

        Assert.True(economics.HasNoFinancialEffect);
        Assert.Equal(Horizon, economics.TimeHorizon);
    }

    private static OpportunityEconomics Create(
        decimal cost = 100m,
        decimal revenue = 120m,
        decimal capital = 100m,
        decimal probability = 0.5m) =>
        OpportunityEconomics.Create(
            Money.Create(cost, Currency.Usd),
            Money.Create(revenue, Currency.Usd),
            Money.Create(capital, Currency.Usd),
            Percentage.FromRatio(probability),
            Horizon);
}
