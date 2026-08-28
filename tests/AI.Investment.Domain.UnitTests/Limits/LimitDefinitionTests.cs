using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Limits;

/// <summary>
/// A limit and the set it belongs to. A limit that compares the wrong dimension never binds, and
/// its absence is invisible, so the constructors refuse the mismatch rather than storing it.
/// </summary>
public sealed class LimitDefinitionTests
{
    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    [Theory]
    [InlineData(LimitKind.MaxPositionSize)]
    [InlineData(LimitKind.MaxTotalExposure)]
    [InlineData(LimitKind.MaxDailyLoss)]
    [InlineData(LimitKind.MaxDrawdown)]
    [InlineData(LimitKind.MaxCostPerCycle)]
    public void A_money_limit_accepts_the_kinds_denominated_in_money(LimitKind kind)
    {
        var limit = Limit.OfMoney(kind, Usd(100m));

        Assert.Equal(kind, limit.Kind);
        Assert.Equal(100m, limit.Amount!.Amount);
        Assert.Null(limit.Count);
        Assert.Null(limit.Duration);
        Assert.Null(limit.Ratio);
    }

    [Theory]
    [InlineData(LimitKind.MaxActionsPerCapabilityPerDay)]
    [InlineData(LimitKind.MaxConcentration)]
    [InlineData(LimitKind.CooldownAfterLoss)]
    [InlineData(LimitKind.InstrumentAllowList)]
    public void A_money_limit_refuses_a_kind_that_is_not_money(LimitKind kind)
    {
        Assert.Throws<DomainValidationException>(() => Limit.OfMoney(kind, Usd(100m)));
    }

    [Fact]
    public void A_negative_ceiling_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            Limit.OfMoney(LimitKind.MaxPositionSize, Usd(-1m)));
    }

    [Fact]
    public void The_unknown_kind_is_not_configurable()
    {
        Assert.Throws<DomainValidationException>(() => Limit.OfMoney(LimitKind.Unknown, Usd(1m)));
    }

    [Fact]
    public void A_count_limit_refuses_a_kind_that_is_not_a_count()
    {
        Assert.Throws<DomainValidationException>(() =>
            Limit.OfCount(LimitKind.MaxPositionSize, 5));
    }

    [Fact]
    public void A_duration_limit_refuses_a_kind_that_is_not_time()
    {
        Assert.Throws<DomainValidationException>(() =>
            Limit.OfDuration(LimitKind.MaxDrawdown, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_ratio_limit_refuses_a_kind_that_is_not_a_ratio()
    {
        Assert.Throws<DomainValidationException>(() =>
            Limit.OfRatio(LimitKind.MaxDailyLoss, Percentage.FromRatio(0.5m)));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_concentration_outside_zero_to_one_is_refused(double ratio)
    {
        Assert.Throws<DomainValidationException>(() =>
            Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio((decimal)ratio)));
    }

    [Fact]
    public void An_empty_set_restricts_no_instrument_and_that_is_deliberate()
    {
        Assert.False(LimitSet.Empty.RestrictsInstruments);
        Assert.True(LimitSet.Empty.Allows("ANYTHING"));
        Assert.False(LimitSet.Empty.RefusesEverything);
    }

    [Fact]
    public void The_fail_closed_set_is_not_the_empty_set()
    {
        Assert.True(LimitSet.FailClosed.RefusesEverything);
        Assert.False(LimitSet.Empty.RefusesEverything);
    }

    [Fact]
    public void An_allow_list_admits_only_what_it_names_and_ignores_case()
    {
        var set = LimitSet.Create([], ["AAPL", "msft"]);

        Assert.True(set.RestrictsInstruments);
        Assert.True(set.Allows("aapl"));
        Assert.True(set.Allows("MSFT"));
        Assert.False(set.Allows("TSLA"));
        Assert.False(set.Allows(null));
    }

    [Fact]
    public void Two_limits_of_the_same_kind_and_scope_are_refused_rather_than_resolved()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            LimitSet.Create(
            [
                Limit.OfMoney(LimitKind.MaxPositionSize, Usd(100m)),
                Limit.OfMoney(LimitKind.MaxPositionSize, Usd(200m)),
            ]));

        Assert.Contains("evaluation order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capability_scoped_limit_wins_over_a_global_one_of_the_same_kind()
    {
        var set = LimitSet.Create(
        [
            Limit.OfMoney(LimitKind.MaxPositionSize, Usd(1000m)),
            Limit.OfMoney(LimitKind.MaxPositionSize, Usd(10m), Capability.SimulatedExecution),
        ]);

        Assert.Equal(10m, set.For(LimitKind.MaxPositionSize, Capability.SimulatedExecution)!.Amount!.Amount);
        Assert.Equal(1000m, set.For(LimitKind.MaxPositionSize, Capability.DataIngestion)!.Amount!.Amount);
    }

    [Fact]
    public void A_kind_nobody_configured_resolves_to_nothing()
    {
        Assert.Null(LimitSet.Empty.For(LimitKind.MaxDrawdown, Capability.SimulatedExecution));
    }

    [Fact]
    public void A_breach_must_say_what_was_exceeded()
    {
        Assert.Throws<DomainValidationException>(() =>
            LimitBreach.Create(LimitKind.MaxDailyLoss, "   "));
    }
}
