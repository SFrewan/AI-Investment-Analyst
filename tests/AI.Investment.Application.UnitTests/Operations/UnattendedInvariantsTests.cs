using AI.Investment.Application.Operations;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// The four things unattended operation is judged on, and what each of them actually measures.
/// </summary>
/// <remarks>
/// Three of the four are absences, and an absence is easy to claim and hard to demonstrate. The tests
/// here exist mostly to pin down what the absences are <em>not</em>: "no duplicate actions" is not a
/// suppression counter of zero, and "shadow data accumulating" is not a demand for measurements in a
/// period where nothing was gated.
/// </remarks>
public sealed class UnattendedInvariantsTests
{
    private static readonly DateTime From = new(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime To = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

    private static UnattendedRunCounts Clean() => new(
        From,
        To,
        CyclesStarted: 120,
        CyclesCompleted: 100,
        CyclesSuspended: 20,
        DuplicateCyclesSuppressed: 340,
        ActionsExecuted: 40,
        DuplicateActionsSuppressed: 3,
        ModelSpend: Money.Create(12.50m, Currency.Usd),
        SpendCeiling: Money.Create(50m, Currency.Usd),
        EscalationsRaised: 20,
        EscalationsUnhandled: 0,
        ShadowDecisions: 40,
        OutboxAbandoned: 0);

    [Fact]
    public void A_clean_period_holds_every_invariant()
    {
        var report = UnattendedInvariants.Evaluate(Clean());

        Assert.True(report.Holds);
        Assert.Empty(report.Failures);
        Assert.True(report.NoUnhandledEscalation);
        Assert.True(report.NoRunawayCost);
        Assert.True(report.NoLostMessages);
        Assert.True(report.ShadowDataAccumulating);
    }

    /// <summary>
    /// A suppressed duplicate is the control working, so a positive count is evidence rather than a
    /// failure. The invariant is that no effect ran twice, not that nothing tried.
    /// </summary>
    [Fact]
    public void Suppressed_duplicates_are_evidence_that_the_control_worked()
    {
        var report = UnattendedInvariants.Evaluate(
            Clean() with { DuplicateActionsSuppressed = 900, DuplicateCyclesSuppressed = 20_000 });

        Assert.True(report.Holds);
    }

    [Fact]
    public void Spending_more_than_the_ceiling_fails_the_period()
    {
        var report = UnattendedInvariants.Evaluate(
            Clean() with { ModelSpend = Money.Create(51m, Currency.Usd) });

        Assert.False(report.Holds);
        Assert.False(report.NoRunawayCost);
        Assert.Contains(report.Failures, failure =>
            failure.Contains("exceeded the ceiling", StringComparison.Ordinal));
    }

    /// <summary>A ceiling that cannot be compared has not held.</summary>
    [Fact]
    public void Spend_in_a_currency_the_ceiling_is_not_in_fails_the_period()
    {
        var report = UnattendedInvariants.Evaluate(
            Clean() with { ModelSpend = Money.Create(1m, Currency.Create("EUR")) });

        Assert.False(report.Holds);
        Assert.False(report.NoRunawayCost);
    }

    /// <summary>
    /// An operator who stops answering is the way a human-in-the-loop control fails in practice, so
    /// one unanswered expiry fails the period.
    /// </summary>
    [Fact]
    public void One_unanswered_escalation_past_its_expiry_fails_the_period()
    {
        var report = UnattendedInvariants.Evaluate(Clean() with { EscalationsUnhandled = 1 });

        Assert.False(report.Holds);
        Assert.False(report.NoUnhandledEscalation);
    }

    [Fact]
    public void An_abandoned_message_fails_the_period()
    {
        var report = UnattendedInvariants.Evaluate(Clean() with { OutboxAbandoned = 1 });

        Assert.False(report.Holds);
        Assert.False(report.NoLostMessages);
        Assert.Contains(report.Failures, failure =>
            failure.Contains("was not said", StringComparison.Ordinal));
    }

    [Fact]
    public void Gating_actions_without_recording_any_shadow_measurement_fails_the_period()
    {
        var report = UnattendedInvariants.Evaluate(Clean() with { ShadowDecisions = 0 });

        Assert.False(report.Holds);
        Assert.False(report.ShadowDataAccumulating);
    }

    /// <summary>
    /// A period in which nothing was gated has nothing to shadow, and demanding measurements anyway
    /// would push somebody towards manufacturing them.
    /// </summary>
    [Fact]
    public void A_quiet_period_is_not_failed_for_having_no_measurements()
    {
        var report = UnattendedInvariants.Evaluate(
            Clean() with { ActionsExecuted = 0, ShadowDecisions = 0 });

        Assert.True(report.Holds);
        Assert.True(report.ShadowDataAccumulating);
    }
}
