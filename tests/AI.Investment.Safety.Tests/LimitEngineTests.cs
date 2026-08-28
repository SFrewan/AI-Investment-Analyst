using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The pre-execution ceilings. These are the checks standing between a defect and a loss, so every
/// kind is exercised, in both directions, and the fail-closed paths are asserted by name.
/// </summary>
public sealed class LimitEngineTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    [Fact]
    public void With_no_limits_configured_nothing_is_refused()
    {
        var verdict = LimitEngine.Evaluate(
            Proposal(exposure: 1_000_000m),
            Flat(),
            LimitSet.Empty,
            Now);

        Assert.True(verdict.IsAllowed);
        Assert.Empty(verdict.Breaches);
        Assert.Equal("Within every configured limit.", verdict.Explain());
    }

    [Fact]
    public void A_set_that_could_not_be_read_refuses_everything()
    {
        var verdict = LimitEngine.Evaluate(Proposal(), Flat(), LimitSet.FailClosed, Now);

        Assert.False(verdict.IsAllowed);
        Assert.Equal(LimitKind.Unknown, Assert.Single(verdict.Breaches).Kind);
    }

    [Fact]
    public void An_action_within_every_ceiling_is_permitted()
    {
        var verdict = LimitEngine.Evaluate(Proposal(exposure: 100m), Flat(), FullSet(), Now);

        Assert.True(verdict.IsAllowed);
    }

    [Fact]
    public void An_instrument_off_the_allow_list_is_refused()
    {
        var limits = LimitSet.Create([], ["MSFT"]);

        var verdict = LimitEngine.Evaluate(Proposal(instrument: "AAPL"), Flat(), limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.InstrumentAllowList);
    }

    [Fact]
    public void An_instrument_on_the_allow_list_is_permitted()
    {
        var limits = LimitSet.Create([], ["AAPL"]);

        Assert.True(LimitEngine.Evaluate(Proposal(instrument: "AAPL"), Flat(), limits, Now).IsAllowed);
    }

    [Fact]
    public void A_position_above_the_single_action_ceiling_is_refused()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxPositionSize, Usd(500m))]);

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 501m), Flat(), limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxPositionSize);
    }

    [Fact]
    public void A_position_exactly_at_the_single_action_ceiling_is_permitted()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxPositionSize, Usd(500m))]);

        Assert.True(LimitEngine.Evaluate(Proposal(exposure: 500m), Flat(), limits, Now).IsAllowed);
    }

    [Fact]
    public void Total_exposure_is_checked_against_what_the_action_would_add()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxTotalExposure, Usd(1_000m))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd, Usd(900m), Usd(10_000m), Usd(10_000m), Usd(0m), Usd(0m));

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 200m), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxTotalExposure);
    }

    [Fact]
    public void A_day_that_has_already_lost_its_allowance_stops_further_action()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxDailyLoss, Usd(100m))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd, Usd(0m), Usd(10_000m), Usd(9_900m), Usd(100m), Usd(0m));

        var verdict = LimitEngine.Evaluate(Proposal(), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxDailyLoss);
    }

    [Fact]
    public void A_drawdown_at_the_ceiling_stops_further_action()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxDrawdown, Usd(1_000m))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd, Usd(0m), Usd(10_000m), Usd(9_000m), Usd(0m), Usd(0m));

        var verdict = LimitEngine.Evaluate(Proposal(), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxDrawdown);
    }

    [Fact]
    public void A_capability_that_has_used_its_daily_actions_is_refused()
    {
        var limits = LimitSet.Create(
            [Limit.OfCount(LimitKind.MaxActionsPerCapabilityPerDay, 2, Capability.SimulatedExecution)]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(0m),
            Usd(10_000m),
            Usd(10_000m),
            Usd(0m),
            Usd(0m),
            actionsToday: new Dictionary<Capability, int> { [Capability.SimulatedExecution] = 2 });

        var verdict = LimitEngine.Evaluate(Proposal(), snapshot, limits, Now);

        Assert.Contains(
            verdict.Breaches,
            breach => breach.Kind == LimitKind.MaxActionsPerCapabilityPerDay);
    }

    [Fact]
    public void A_cycle_that_would_exceed_its_spend_is_refused()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxCostPerCycle, Usd(10m))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd, Usd(0m), Usd(10_000m), Usd(10_000m), Usd(0m), Usd(9m));

        var verdict = LimitEngine.Evaluate(Proposal(cost: 2m), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxCostPerCycle);
    }

    [Fact]
    public void Concentration_is_measured_as_a_share_of_equity()
    {
        var limits = LimitSet.Create(
            [Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(0.04m))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(100m),
            Usd(10_000m),
            Usd(10_000m),
            Usd(0m),
            Usd(0m),
            exposureByInstrument: new Dictionary<string, Money> { ["AAPL"] = Usd(100m) });

        var verdict = LimitEngine.Evaluate(
            Proposal(instrument: "AAPL", exposure: 400m),
            snapshot,
            limits,
            Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxConcentration);
    }

    [Fact]
    public void An_opening_position_in_a_flat_book_is_not_treated_as_the_whole_book()
    {
        var limits = LimitSet.Create(
            [Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(0.25m))]);

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 100m), Flat(), limits, Now);

        Assert.True(verdict.IsAllowed);
    }

    [Fact]
    public void Concentration_cannot_be_measured_against_a_book_holding_no_equity()
    {
        var limits = LimitSet.Create(
            [Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(0.25m))]);

        var snapshot = ExposureSnapshot.Flat(Currency.Usd, Usd(0m));

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 1m), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.MaxConcentration);
    }

    [Fact]
    public void An_action_with_no_exposure_cannot_concentrate_anything()
    {
        var limits = LimitSet.Create(
            [Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(0.01m))]);

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 0m), Flat(), limits, Now);

        Assert.DoesNotContain(verdict.Breaches, breach => breach.Kind == LimitKind.MaxConcentration);
    }

    [Fact]
    public void A_cooldown_after_a_loss_holds_for_its_full_duration()
    {
        var limits = LimitSet.Create(
            [Limit.OfDuration(LimitKind.CooldownAfterLoss, TimeSpan.FromMinutes(60))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(0m),
            Usd(10_000m),
            Usd(10_000m),
            Usd(0m),
            Usd(0m),
            lastRealisedLossAtUtc: Now.AddMinutes(-30));

        var verdict = LimitEngine.Evaluate(Proposal(), snapshot, limits, Now);

        Assert.Contains(verdict.Breaches, breach => breach.Kind == LimitKind.CooldownAfterLoss);
    }

    [Fact]
    public void A_cooldown_that_has_elapsed_stops_binding()
    {
        var limits = LimitSet.Create(
            [Limit.OfDuration(LimitKind.CooldownAfterLoss, TimeSpan.FromMinutes(60))]);

        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(0m),
            Usd(10_000m),
            Usd(10_000m),
            Usd(0m),
            Usd(0m),
            lastRealisedLossAtUtc: Now.AddMinutes(-61));

        Assert.True(LimitEngine.Evaluate(Proposal(), snapshot, limits, Now).IsAllowed);
    }

    [Fact]
    public void A_limit_that_cannot_be_compared_is_refused_rather_than_skipped()
    {
        var limits = LimitSet.Create(
            [Limit.OfMoney(LimitKind.MaxPositionSize, Money.Create(1m, Currency.Create("EUR")))]);

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 1_000_000m), Flat(), limits, Now);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(
            verdict.Breaches,
            breach => breach.Explanation.Contains("cannot be compared", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_breach_is_reported_not_only_the_first()
    {
        var limits = LimitSet.Create(
            [
                Limit.OfMoney(LimitKind.MaxPositionSize, Usd(10m)),
                Limit.OfMoney(LimitKind.MaxTotalExposure, Usd(20m)),
            ],
            ["MSFT"]);

        var verdict = LimitEngine.Evaluate(
            Proposal(instrument: "AAPL", exposure: 1_000m),
            Flat(),
            limits,
            Now);

        Assert.Equal(3, verdict.Breaches.Count);
        Assert.Contains("Refused by", verdict.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_utc_evaluation_time_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            LimitEngine.Evaluate(
                Proposal(),
                Flat(),
                LimitSet.Empty,
                DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }

    private static ExposureSnapshot Flat() =>
        ExposureSnapshot.Flat(Currency.Usd, Usd(10_000m));

    private static LimitSet FullSet() =>
        LimitSet.Create(
        [
            Limit.OfMoney(LimitKind.MaxPositionSize, Usd(5_000m)),
            Limit.OfMoney(LimitKind.MaxTotalExposure, Usd(25_000m)),
            Limit.OfMoney(LimitKind.MaxDailyLoss, Usd(500m)),
            Limit.OfMoney(LimitKind.MaxDrawdown, Usd(2_500m)),
            Limit.OfMoney(LimitKind.MaxCostPerCycle, Usd(50m)),
            Limit.OfCount(LimitKind.MaxActionsPerCapabilityPerDay, 25),
            Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(0.25m)),
            Limit.OfDuration(LimitKind.CooldownAfterLoss, TimeSpan.FromMinutes(60)),
        ]);

    private static ActionProposal Proposal(
        string instrument = "AAPL",
        decimal exposure = 100m,
        decimal cost = 0m) =>
        ActionProposal.Create(
            CorrelationId.New(),
            Capability.SimulatedExecution,
            ActionType.Create("execution.simulated-order"),
            ActionTarget.Create("Instrument", instrument),
            new LimitTestParameters(instrument),
            ActionEconomics.Create(Usd(cost), Usd(exposure), ReversibilityClass.ReversibleWithCost),
            ProposedBy.Service("limit-tests", "1.0"),
            Guid.NewGuid().ToString("n"),
            Now);

    private sealed record LimitTestParameters(string Instrument) : IActionParameters
    {
        public string Describe() => "instrument=" + Instrument;
    }
}
