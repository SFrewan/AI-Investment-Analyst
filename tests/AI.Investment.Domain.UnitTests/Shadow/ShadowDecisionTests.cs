using System.Reflection;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Shadow;

/// <summary>
/// A measurement of what a more autonomous system would have done, and the reasons it can never
/// become the doing of it.
/// </summary>
public sealed class ShadowDecisionTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static ShadowDecision Record(
        AutonomyMode actualMode = AutonomyMode.PrepareForApproval,
        PolicyOutcome actualOutcome = PolicyOutcome.RequireApproval,
        AutonomyMode shadowMode = AutonomyMode.AutoExecuteBounded,
        PolicyOutcome shadowOutcome = PolicyOutcome.Execute) =>
        ShadowDecision.Record(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Capability.SimulatedExecution,
            "execution.simulated-order",
            RiskTier.Medium,
            Money.Create(1_000m, Currency.Usd),
            actualMode,
            actualOutcome,
            shadowMode,
            shadowOutcome,
            "the shadow gate permitted unattended execution",
            Now);

    [Fact]
    public void A_measurement_records_both_answers_and_the_difference_between_them()
    {
        var decision = Record();

        Assert.True(decision.WouldHaveExecuted);
        Assert.False(decision.Agreed);
        Assert.Equal(PolicyOutcome.RequireApproval, decision.ActualOutcome);
        Assert.Equal(PolicyOutcome.Execute, decision.ShadowOutcome);
    }

    [Fact]
    public void Agreement_is_recorded_as_plainly_as_disagreement()
    {
        var decision = Record(shadowOutcome: PolicyOutcome.RequireApproval);

        Assert.True(decision.Agreed);
        Assert.False(decision.WouldHaveExecuted);
    }

    /// <summary>
    /// A measurement of the level already in force measures nothing, and a row saying so would
    /// dilute the count a promotion is judged on.
    /// </summary>
    [Fact]
    public void A_shadow_decision_must_measure_a_higher_level_than_the_one_in_force()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            Record(actualMode: AutonomyMode.AutoExecuteBounded, shadowMode: AutonomyMode.AutoExecuteBounded));

        Assert.Equal("ShadowDecision.MeasuresHigherAutonomy", error.Rule);

        Assert.Throws<DomainRuleViolationException>(() =>
            Record(actualMode: AutonomyMode.ContinuousBounded, shadowMode: AutonomyMode.Advise));
    }

    [Fact]
    public void A_measurement_names_the_proposal_the_action_type_and_the_reason()
    {
        Assert.Throws<DomainValidationException>(() => ShadowDecision.Record(
            null, Guid.Empty, Capability.Analysis, "t", RiskTier.Low, Money.ZeroUsd,
            AutonomyMode.Advise, PolicyOutcome.Deny, AutonomyMode.PrepareForApproval,
            PolicyOutcome.Deny, "why", Now));

        Assert.Throws<DomainValidationException>(() => ShadowDecision.Record(
            null, Guid.NewGuid(), Capability.Analysis, "  ", RiskTier.Low, Money.ZeroUsd,
            AutonomyMode.Advise, PolicyOutcome.Deny, AutonomyMode.PrepareForApproval,
            PolicyOutcome.Deny, "why", Now));

        Assert.Throws<DomainValidationException>(() => ShadowDecision.Record(
            null, Guid.NewGuid(), Capability.Analysis, "t", RiskTier.Low, Money.ZeroUsd,
            AutonomyMode.Advise, PolicyOutcome.Deny, AutonomyMode.PrepareForApproval,
            PolicyOutcome.Deny, "  ", Now));
    }

    /// <summary>
    /// The property that makes shadow mode safe: there is nothing on this type that does anything.
    /// </summary>
    /// <remarks>
    /// Asserted by reflection rather than by reading the file, so that a method added later has to
    /// pass this test. A shadow decision cannot become an action by being handed to the wrong thing,
    /// because there is nothing to hand it to.
    /// </remarks>
    [Fact]
    public void A_shadow_decision_has_no_method_that_does_anything()
    {
        var behaviour = typeof(ShadowDecision)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Where(method => !string.Equals(method.Name, nameof(ToString), StringComparison.Ordinal))
            .ToList();

        Assert.Empty(behaviour);

        // And nothing on it holds a delegate, a task or anything else that could be invoked.
        var invocable = typeof(ShadowDecision)
            .GetProperties()
            .Where(property =>
                typeof(Delegate).IsAssignableFrom(property.PropertyType) ||
                typeof(System.Threading.Tasks.Task).IsAssignableFrom(property.PropertyType))
            .ToList();

        Assert.Empty(invocable);
    }

    [Fact]
    public void A_measurement_is_taken_at_a_utc_instant() =>
        Assert.Throws<DomainValidationException>(() => ShadowDecision.Record(
            null, Guid.NewGuid(), Capability.Analysis, "t", RiskTier.Low, Money.ZeroUsd,
            AutonomyMode.Advise, PolicyOutcome.Deny, AutonomyMode.PrepareForApproval,
            PolicyOutcome.Deny, "why", DateTime.SpecifyKind(Now, DateTimeKind.Local)));
}
