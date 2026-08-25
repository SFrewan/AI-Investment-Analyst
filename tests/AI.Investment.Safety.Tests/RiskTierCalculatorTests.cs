using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Risk is computed, never asserted by whoever proposes the action.
/// </summary>
public sealed class RiskTierCalculatorTests
{
    private static ActionEconomics Economics(
        ReversibilityClass reversibility = ReversibilityClass.Reversible,
        decimal cost = 0m,
        decimal exposure = 0m) =>
        ActionEconomics.Create(Money.Create(cost, "USD"), Money.Create(exposure, "USD"), reversibility);

    [Fact]
    public void A_harmless_reversible_reference_data_action_is_low_risk() =>
        Assert.Equal(RiskTier.Low, RiskTierCalculator.Calculate(Capability.ReferenceDataManagement, Economics()));

    /// <summary>
    /// Reversibility dominates amount: a cheap irreversible action outranks an expensive
    /// reversible one, because the question is not "how much" but "can this be taken back".
    /// </summary>
    [Fact]
    public void Irreversibility_outranks_a_large_reversible_exposure()
    {
        var cheapIrreversible = RiskTierCalculator.Calculate(
            Capability.ReferenceDataManagement,
            Economics(ReversibilityClass.Irreversible));

        var expensiveReversible = RiskTierCalculator.Calculate(
            Capability.ReferenceDataManagement,
            Economics(exposure: 1_000_000m));

        Assert.Equal(RiskTier.High, cheapIrreversible);
        Assert.Equal(RiskTier.Medium, expensiveReversible);
        Assert.True(cheapIrreversible > expensiveReversible);
    }

    [Theory]
    [InlineData(ReversibilityClass.Reversible, RiskTier.Low)]
    [InlineData(ReversibilityClass.ReversibleWithCost, RiskTier.Medium)]
    [InlineData(ReversibilityClass.Irreversible, RiskTier.High)]
    public void Reversibility_sets_a_floor(ReversibilityClass reversibility, RiskTier expected) =>
        Assert.Equal(
            expected,
            RiskTierCalculator.Calculate(Capability.ReferenceDataManagement, Economics(reversibility)));

    [Fact]
    public void Any_non_zero_exposure_lifts_the_tier_to_at_least_medium() =>
        Assert.Equal(
            RiskTier.Medium,
            RiskTierCalculator.Calculate(Capability.DataIngestion, Economics(exposure: 0.01m)));

    [Theory]
    [InlineData(Capability.PolicyAdministration)]
    [InlineData(Capability.AutonomyAdministration)]
    [InlineData(Capability.FinancialExecution)]
    public void Capabilities_that_govern_the_system_itself_are_always_critical(Capability capability) =>
        Assert.Equal(RiskTier.Critical, RiskTierCalculator.Calculate(capability, Economics()));

    [Fact]
    public void Novelty_escalates_by_one_tier() =>
        Assert.Equal(
            RiskTier.Medium,
            RiskTierCalculator.Calculate(Capability.ReferenceDataManagement, Economics(), isNovel: true));

    [Fact]
    public void Novelty_cannot_escalate_beyond_critical() =>
        Assert.Equal(
            RiskTier.Critical,
            RiskTierCalculator.Calculate(Capability.PolicyAdministration, Economics(), isNovel: true));

    /// <summary>
    /// Fail closed: a capability added to the enum without updating the calculator must be
    /// treated as maximally dangerous, not defaulted to Low.
    /// </summary>
    [Fact]
    public void An_unrecognised_capability_is_treated_as_critical() =>
        Assert.Equal(RiskTier.Critical, RiskTierCalculator.Calculate((Capability)9999, Economics()));

    [Fact]
    public void An_unrecognised_reversibility_class_is_treated_as_critical() =>
        Assert.Equal(
            RiskTier.Critical,
            RiskTierCalculator.Calculate(
                Capability.ReferenceDataManagement,
                ActionEconomics.Create(Money.ZeroUsd, Money.ZeroUsd, (ReversibilityClass)9999)));

    /// <summary>
    /// There is no constructor or setter through which a proposer could supply its own tier.
    /// </summary>
    [Fact]
    public void RiskTier_has_no_public_setter_on_the_proposal()
    {
        var property = typeof(ActionProposal).GetProperty(nameof(ActionProposal.RiskTier));

        Assert.NotNull(property);
        Assert.Null(property!.SetMethod);
    }
}
