using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The limit engine's argument guards and the wording of its breaches, and the lookup rules of the
/// set it reads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LimitEngineTests"/> covers which ceilings bind. This covers what happens at the edges
/// of the mechanism: a call with a missing argument, and what a breach actually says.
/// </para>
/// <para>
/// A breach explanation is not decoration. It is the whole of what an operator sees when the system
/// declines to act, and "the limit is configured in EUR but the action is in USD" and "the position
/// is too large" call for opposite responses - one is a misconfiguration, the other is the control
/// doing its job.
/// </para>
/// </remarks>
public sealed class LimitBoundaryTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    // ---- Argument guards ---------------------------------------------------------------------

    [Fact]
    public void An_evaluation_without_a_proposal_is_refused() =>
        Assert.Throws<ArgumentNullException>(() =>
            LimitEngine.Evaluate(null!, Flat(), Configured(), Now));

    [Fact]
    public void An_evaluation_without_a_snapshot_is_refused() =>
        Assert.Throws<ArgumentNullException>(() =>
            LimitEngine.Evaluate(Proposal(), null!, Configured(), Now));

    [Fact]
    public void An_evaluation_without_a_limit_set_is_refused() =>
        Assert.Throws<ArgumentNullException>(() =>
            LimitEngine.Evaluate(Proposal(), Flat(), null!, Now));

    // ---- What a breach says -------------------------------------------------------------------

    /// <summary>
    /// The fail-closed refusal must say that the limits could not be read, not merely that something
    /// was exceeded. They are different incidents: one is a configuration or storage fault that
    /// needs fixing, the other is the system working.
    /// </summary>
    [Fact]
    public void A_set_that_could_not_be_read_says_so_rather_than_naming_a_ceiling()
    {
        var verdict = LimitEngine.Evaluate(Proposal(), Flat(), LimitSet.FailClosed, Now);

        var breach = Assert.Single(verdict.Breaches);

        Assert.Equal(LimitKind.Unknown, breach.Kind);
        Assert.Contains("could not be read", breach.Explanation, StringComparison.Ordinal);
        Assert.Contains(
            "cannot determine its own ceilings must not act",
            breach.Explanation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_that_cannot_be_compared_names_both_currencies()
    {
        var limits = LimitSet.Create(
            [Limit.OfMoney(LimitKind.MaxPositionSize, Money.Create(1m, Currency.Create("EUR")))]);

        var verdict = LimitEngine.Evaluate(Proposal(exposure: 1_000m), Flat(), limits, Now);

        var breach = verdict.Breaches.Single(candidate => candidate.Kind == LimitKind.MaxPositionSize);

        Assert.Contains("configured in EUR", breach.Explanation, StringComparison.Ordinal);
        Assert.Contains("action is in USD", breach.Explanation, StringComparison.Ordinal);
        Assert.Contains("refused rather than skipped", breach.Explanation, StringComparison.Ordinal);
    }

    // ---- The set ------------------------------------------------------------------------------

    [Fact]
    public void A_set_cannot_be_built_from_nothing() =>
        Assert.Throws<ArgumentNullException>(() => LimitSet.Create(null!));

    [Fact]
    public void A_missing_limit_inside_a_set_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => LimitSet.Create([null!]));

    /// <summary>
    /// Two limits of the same kind and scope are refused, and the refusal names the scope - the
    /// wildcard being what says "this one applies to everything", which is the part a reader needs
    /// in order to find the duplicate in a configuration file.
    /// </summary>
    [Fact]
    public void Two_global_limits_of_the_same_kind_are_refused_and_the_scope_is_named()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            LimitSet.Create(
                [
                    Limit.OfMoney(LimitKind.MaxPositionSize, Usd(100m)),
                    Limit.OfMoney(LimitKind.MaxPositionSize, Usd(200m)),
                ]));

        Assert.Contains("MaxPositionSize/*", error.Message, StringComparison.Ordinal);
        Assert.Contains("evaluation order", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A global limit applies to every capability, but only for its own kind. A lookup that returned
    /// it for a different kind would compare a position-size ceiling against a daily loss, which is
    /// the silent dimension mismatch the limit model exists to prevent.
    /// </summary>
    [Fact]
    public void A_global_limit_does_not_answer_a_lookup_for_another_kind()
    {
        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxPositionSize, Usd(100m))]);

        Assert.NotNull(limits.For(LimitKind.MaxPositionSize, Capability.SimulatedExecution));
        Assert.Null(limits.For(LimitKind.MaxDailyLoss, Capability.SimulatedExecution));
    }

    [Fact]
    public void A_set_says_how_many_limits_it_holds_and_whether_instruments_are_restricted()
    {
        var restricted = LimitSet.Create(
            [Limit.OfMoney(LimitKind.MaxPositionSize, Usd(100m))],
            ["AAPL"]);

        var described = restricted.ToString();

        Assert.Contains("1 limits", described, StringComparison.Ordinal);
        Assert.Contains("1 instruments", described, StringComparison.Ordinal);

        Assert.Contains(
            "no instrument restriction",
            LimitSet.Empty.ToString(),
            StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    private static ExposureSnapshot Flat() => ExposureSnapshot.Flat(Currency.Usd, Usd(10_000m));

    /// <summary>
    /// A set that exercises every check, so an argument guard cannot appear to hold merely because
    /// no rule reached the argument.
    /// </summary>
    private static LimitSet Configured() =>
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
            ],
            ["AAPL"]);

    private static ActionProposal Proposal(string instrument = "AAPL", decimal exposure = 100m) =>
        ActionProposal.Create(
            CorrelationId.New(),
            Capability.SimulatedExecution,
            ActionType.Create("execution.simulated-order"),
            ActionTarget.Create("Instrument", instrument),
            new BoundaryTestParameters(instrument),
            ActionEconomics.Create(Usd(0m), Usd(exposure), ReversibilityClass.ReversibleWithCost),
            ProposedBy.Service("limit-boundary-tests", "1.0"),
            Guid.NewGuid().ToString("n"),
            Now);

    private sealed record BoundaryTestParameters(string Instrument) : IActionParameters
    {
        public string Describe() => "instrument=" + Instrument;
    }
}
