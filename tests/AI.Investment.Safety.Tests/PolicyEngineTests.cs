using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The policy engine is the single place where "may this happen?" is answered. These tests are
/// the reason to believe it works.
/// </summary>
public sealed class PolicyEngineTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PolicyEngine _engine = new();

    // ---- Kill switch ---------------------------------------------------------------------

    [Fact]
    public void An_engaged_kill_switch_denies_everything()
    {
        var decision = Evaluate(
            Proposal(),
            Context(KillSwitchState.Engaged, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(PolicyEngine.KillSwitchPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
    }

    /// <summary>
    /// The fail-closed property. A control whose state cannot be read must behave as though it
    /// were engaged - a control that fails open is not a control.
    /// </summary>
    [Fact]
    public void An_unknown_kill_switch_state_denies_exactly_like_an_engaged_one()
    {
        var decision = Evaluate(
            Proposal(),
            Context(KillSwitchState.Unknown, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    // ---- Missing or disabled policy ------------------------------------------------------

    [Fact]
    public void A_capability_with_no_policy_is_denied()
    {
        var decision = Evaluate(Proposal(), Context(KillSwitchState.Disengaged));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(PolicyEngine.CapabilityDefinedPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
    }

    [Fact]
    public void A_fail_closed_context_denies_everything()
    {
        var decision = _engine.Evaluate(Proposal(), PolicyContext.FailClosed(), Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public void A_disabled_capability_is_denied()
    {
        var decision = Evaluate(
            Proposal(),
            Context(KillSwitchState.Disengaged, CapabilityPolicy.Disabled(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    // ---- Structural prohibitions: not configurable ---------------------------------------

    /// <summary>
    /// An agent that can change policy can grant itself anything. This is the rule that makes
    /// every other rule meaningful, so it must hold even when configuration says otherwise.
    /// </summary>
    [Theory]
    [InlineData(Capability.PolicyAdministration)]
    [InlineData(Capability.AutonomyAdministration)]
    [InlineData(Capability.ApprovalAdministration)]
    public void An_ai_proposer_may_never_administer_safety_even_when_configuration_permits_it(
        Capability capability)
    {
        var permissive = CapabilityPolicy.Create(
            capability,
            enabled: true,
            RiskTier.Critical,
            allowIrreversibleAutoExecute: true,
            allowAiProposers: true);

        var decision = Evaluate(
            Proposal(capability: capability, proposedBy: Agent()),
            Context(KillSwitchState.Disengaged, permissive));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(
            PolicyEngine.AiMayNotAdministerSafetyPolicy,
            decision.EvaluatedPolicies,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Financial_execution_is_refused_regardless_of_configuration()
    {
        var permissive = CapabilityPolicy.Create(
            Capability.FinancialExecution,
            enabled: true,
            RiskTier.Critical,
            allowIrreversibleAutoExecute: true,
            allowAiProposers: true);

        var decision = Evaluate(
            Proposal(capability: Capability.FinancialExecution, proposedBy: ProposedBy.Human("operator")),
            Context(KillSwitchState.Disengaged, permissive));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(
            PolicyEngine.FinancialExecutionUnavailablePolicy,
            decision.EvaluatedPolicies,
            StringComparer.Ordinal);
    }

    // ---- AI proposers --------------------------------------------------------------------

    [Fact]
    public void An_ai_proposer_is_denied_where_the_capability_does_not_allow_one()
    {
        var decision = Evaluate(
            Proposal(proposedBy: Agent()),
            Context(KillSwitchState.Disengaged, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(PolicyEngine.AiProposerAllowedPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
    }

    [Fact]
    public void An_ai_proposer_is_permitted_where_the_capability_allows_one()
    {
        var policy = CapabilityPolicy.Create(
            Capability.ReferenceDataManagement,
            enabled: true,
            RiskTier.Low,
            allowIrreversibleAutoExecute: false,
            allowAiProposers: true);

        var decision = Evaluate(
            Proposal(proposedBy: Agent()),
            Context(KillSwitchState.Disengaged, policy));

        Assert.Equal(PolicyOutcome.Execute, decision.Outcome);
    }

    // ---- Reversibility and risk tier -----------------------------------------------------

    [Fact]
    public void An_irreversible_action_requires_approval_by_default()
    {
        var economics = ActionEconomics.Create(
            Money.ZeroUsd,
            Money.ZeroUsd,
            ReversibilityClass.Irreversible);

        var policy = CapabilityPolicy.Create(
            Capability.ReferenceDataManagement,
            enabled: true,
            RiskTier.Critical);

        var decision = Evaluate(
            Proposal(economics: economics),
            Context(KillSwitchState.Disengaged, policy));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(
            PolicyEngine.IrreversibleRequiresApprovalPolicy,
            decision.EvaluatedPolicies,
            StringComparer.Ordinal);
    }

    [Fact]
    public void A_risk_tier_above_the_ceiling_requires_approval()
    {
        // Exposure lifts the tier to Medium; the ceiling is Low.
        var economics = ActionEconomics.Create(
            Money.ZeroUsd,
            Money.Create(1_000m, "USD"),
            ReversibilityClass.Reversible);

        var decision = Evaluate(
            Proposal(economics: economics),
            Context(KillSwitchState.Disengaged, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(
            PolicyEngine.RiskTierWithinAutoExecutePolicy,
            decision.EvaluatedPolicies,
            StringComparer.Ordinal);
    }

    [Fact]
    public void A_harmless_reversible_action_within_the_ceiling_executes()
    {
        var decision = Evaluate(
            Proposal(),
            Context(KillSwitchState.Disengaged, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(PolicyOutcome.Execute, decision.Outcome);
        Assert.True(decision.PermitsExecution);
    }

    // ---- Totality ------------------------------------------------------------------------

    /// <summary>
    /// Exhaustive over every combination of capability, reversibility, proposer kind and kill
    /// switch state. Proves the engine is total - it always returns one of the three outcomes,
    /// never null and never an exception - and that no combination reaches Execute while the
    /// kill switch is not explicitly disengaged.
    /// </summary>
    [Fact]
    public void Evaluation_is_total_and_never_executes_unless_the_kill_switch_is_disengaged()
    {
        var capabilities = Enum.GetValues<Capability>();
        var reversibilities = Enum.GetValues<ReversibilityClass>();
        var killSwitchStates = Enum.GetValues<KillSwitchState>();
        var proposers = new[] { ProposedBy.Human("operator"), ProposedBy.Service("svc", "1.0"), Agent() };

        var evaluated = 0;

        foreach (var capability in capabilities)
        {
            foreach (var reversibility in reversibilities)
            {
                foreach (var killSwitch in killSwitchStates)
                {
                    foreach (var proposer in proposers)
                    {
                        var economics = ActionEconomics.Create(Money.ZeroUsd, Money.ZeroUsd, reversibility);

                        var mostPermissive = CapabilityPolicy.Create(
                            capability,
                            enabled: true,
                            RiskTier.Critical,
                            allowIrreversibleAutoExecute: true,
                            allowAiProposers: true);

                        var decision = _engine.Evaluate(
                            Proposal(capability: capability, economics: economics, proposedBy: proposer),
                            Context(killSwitch, mostPermissive),
                            Now);

                        Assert.NotNull(decision);
                        Assert.Contains(
                            decision.Outcome,
                            new[] { PolicyOutcome.Execute, PolicyOutcome.RequireApproval, PolicyOutcome.Deny });
                        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                        Assert.NotEmpty(decision.EvaluatedPolicies);

                        if (killSwitch != KillSwitchState.Disengaged)
                        {
                            Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
                        }

                        if (decision.Outcome == PolicyOutcome.Execute)
                        {
                            // Nothing may execute in a safety-administration or financial
                            // capability, however permissive the configuration.
                            Assert.NotEqual(Capability.FinancialExecution, capability);
                            Assert.False(proposer.IsAi && IsSafetyAdministration(capability));
                        }

                        evaluated++;
                    }
                }
            }
        }

        Assert.Equal(capabilities.Length * reversibilities.Length * killSwitchStates.Length * proposers.Length, evaluated);
    }

    [Fact]
    public void Every_decision_is_bound_to_the_proposal_it_was_made_for()
    {
        var proposal = Proposal();
        var other = Proposal();

        var decision = Evaluate(proposal, Context(KillSwitchState.Disengaged, Permissive(Capability.ReferenceDataManagement)));

        Assert.Equal(proposal.ProposalId, decision.ProposalId);
        Assert.Throws<Domain.Exceptions.DomainRuleViolationException>(() => decision.EnsureAuthorises(other));
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static bool IsSafetyAdministration(Capability capability) =>
        capability is Capability.PolicyAdministration
            or Capability.AutonomyAdministration
            or Capability.ApprovalAdministration;

    private PolicyDecision Evaluate(ActionProposal proposal, PolicyContext context) =>
        _engine.Evaluate(proposal, context, Now);

    private static PolicyContext Context(KillSwitchState killSwitch, params CapabilityPolicy[] policies) =>
        PolicyContext.Create("Test", killSwitch, policies);

    private static CapabilityPolicy Permissive(Capability capability) =>
        CapabilityPolicy.Create(capability, enabled: true, RiskTier.Low);

    private static ProposedBy Agent() =>
        ProposedBy.AiAgent("agent.test", "1.0", "prompts/test", "1.0");

    private static ActionProposal Proposal(
        Capability capability = Capability.ReferenceDataManagement,
        ActionEconomics? economics = null,
        ProposedBy? proposedBy = null)
    {
        proposedBy ??= ProposedBy.Service("test", "1.0");

        // An AI proposal must carry confidence and evidence, so supply both when the proposer
        // is an agent - those rules are tested separately in ActionProposalTests.
        var isAi = proposedBy.IsAi;

        return ActionProposal.Create(
            CorrelationId.New(),
            capability,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new TestParameters(),
            economics ?? ActionEconomics.NoFinancialEffect(),
            proposedBy,
            idempotencyKey: Guid.NewGuid().ToString("n"),
            Now,
            evidence: isAi ? [Domain.Evidence.ClaimId.New()] : null,
            confidence: isAi ? Confidence.Create(0.8m) : null);
    }

    private sealed record TestParameters : IActionParameters
    {
        public string Describe() => "test parameters";
    }
}
