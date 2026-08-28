using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The record a policy decision leaves behind: the reason it gives, and the rules it says it
/// evaluated.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PolicyEngineTests"/> pins the outcomes. This file pins the audit trail, which mutation
/// testing showed nothing was checking: every refusal message could be emptied and every test still
/// passed. A decision with a blank reason denies exactly as correctly and is worthless six months
/// later when somebody asks why an action was stopped - and "why" is the entire product of a
/// deterministic gate. The reason string and the evaluated-policy list are outputs, so they are
/// asserted like outputs.
/// </para>
/// <para>
/// The assertions name fragments of meaning rather than whole sentences, so that rewording a message
/// does not break a test while deleting its content does.
/// </para>
/// </remarks>
public sealed class PolicyDecisionReasonTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PolicyEngine _engine = new();

    // ---- Arguments -------------------------------------------------------------------------

    /// <summary>
    /// A missing proposal must be refused at the door rather than dereferenced three rules later,
    /// where the failure would be a null reference in the middle of a safety evaluation.
    /// </summary>
    [Fact]
    public void A_null_proposal_is_refused_before_any_rule_runs() =>
        Assert.Throws<ArgumentNullException>(() =>
            _engine.Evaluate(null!, Context(KillSwitchState.Disengaged, Permissive()), Now));

    [Fact]
    public void A_null_context_is_refused_before_any_rule_runs() =>
        Assert.Throws<ArgumentNullException>(() => _engine.Evaluate(Proposal(), null!, Now));

    // ---- Kill switch -------------------------------------------------------------------------

    /// <summary>
    /// Engaged and Unknown deny identically, and that is the point - but the audit trail must still
    /// distinguish "somebody stopped the system" from "the system could not tell". They are
    /// different incidents with different follow-ups.
    /// </summary>
    [Fact]
    public void An_engaged_kill_switch_and_an_unreadable_one_deny_alike_but_do_not_read_alike()
    {
        var engaged = _engine.Evaluate(
            Proposal(),
            Context(KillSwitchState.Engaged, Permissive()),
            Now);

        var unknown = _engine.Evaluate(
            Proposal(),
            Context(KillSwitchState.Unknown, Permissive()),
            Now);

        Assert.Equal(PolicyOutcome.Deny, engaged.Outcome);
        Assert.Equal(PolicyOutcome.Deny, unknown.Outcome);

        Assert.Contains("kill switch is engaged", engaged.Reason, StringComparison.Ordinal);
        Assert.Contains("could not be determined", unknown.Reason, StringComparison.Ordinal);
        Assert.NotEqual(engaged.Reason, unknown.Reason, StringComparer.Ordinal);
    }

    // ---- Refusal reasons ---------------------------------------------------------------------

    [Fact]
    public void An_undefined_capability_is_refused_by_name_and_by_environment()
    {
        var decision = _engine.Evaluate(Proposal(), Context(KillSwitchState.Disengaged), Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains("ReferenceDataManagement", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("'Test'", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("undefined capability is denied", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_structural_ai_prohibition_says_that_it_is_structural()
    {
        var permissive = CapabilityPolicy.Create(
            Capability.PolicyAdministration,
            enabled: true,
            RiskTier.Critical,
            allowIrreversibleAutoExecute: true,
            allowAiProposers: true);

        var decision = _engine.Evaluate(
            Proposal(capability: Capability.PolicyAdministration, proposedBy: Agent()),
            Context(KillSwitchState.Disengaged, permissive),
            Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains("may never administer the safety system", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("PolicyAdministration", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_financial_execution_refusal_says_no_execution_plane_exists()
    {
        var permissive = CapabilityPolicy.Create(
            Capability.FinancialExecution,
            enabled: true,
            RiskTier.Critical,
            allowIrreversibleAutoExecute: true,
            allowAiProposers: true);

        var decision = _engine.Evaluate(
            Proposal(capability: Capability.FinancialExecution),
            Context(KillSwitchState.Disengaged, permissive),
            Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains("Financial execution is not available", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(
            "structural rather than a consequence of configuration",
            decision.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_irreversible_action_says_why_it_needs_a_human()
    {
        var economics = ActionEconomics.Create(
            Money.ZeroUsd,
            Money.ZeroUsd,
            ReversibilityClass.Irreversible);

        var policy = CapabilityPolicy.Create(
            Capability.ReferenceDataManagement,
            enabled: true,
            RiskTier.Critical);

        var decision = _engine.Evaluate(
            Proposal(economics: economics),
            Context(KillSwitchState.Disengaged, policy),
            Now);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(
            "unattended execution of irreversible actions",
            decision.Reason,
            StringComparison.Ordinal);
        Assert.Contains("'ReferenceDataManagement'", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_above_the_ceiling_names_both_the_tier_and_the_ceiling()
    {
        // Exposure lifts the computed tier to Medium; the configured ceiling is Low.
        var economics = ActionEconomics.Create(
            Money.ZeroUsd,
            Money.Create(1_000m, "USD"),
            ReversibilityClass.Reversible);

        var decision = _engine.Evaluate(
            Proposal(economics: economics),
            Context(KillSwitchState.Disengaged, Permissive()),
            Now);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("Risk tier Medium exceeds", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("ceiling of Low", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("'ReferenceDataManagement'", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permitted_action_says_which_ceiling_it_was_within()
    {
        var decision = _engine.Evaluate(
            Proposal(),
            Context(KillSwitchState.Disengaged, Permissive()),
            Now);

        Assert.Equal(PolicyOutcome.Execute, decision.Outcome);
        Assert.Contains("is within the unattended ceiling", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("no prohibition applies", decision.Reason, StringComparison.Ordinal);
    }

    // ---- The evaluated-policy trail ------------------------------------------------------------

    /// <summary>
    /// Every rule reached is recorded, in the order it was reached.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact ordered sequence rather than a set of <c>Contains</c> checks, because the
    /// ordering is itself a safety property: the structural prohibitions are evaluated before the
    /// configurable rules so that no configuration can pre-empt them. A test that only checked
    /// membership would pass if the order were reversed, and would also pass if a rule silently
    /// stopped recording itself - which is exactly what a mutant demonstrated.
    /// </remarks>
    [Fact]
    public void A_permitted_decision_records_every_rule_it_passed_in_order()
    {
        var decision = _engine.Evaluate(
            Proposal(),
            Context(KillSwitchState.Disengaged, Permissive()),
            Now);

        string[] expected =
        [
            PolicyEngine.KillSwitchPolicy,
            PolicyEngine.CapabilityDefinedPolicy,
            PolicyEngine.AiMayNotAdministerSafetyPolicy,
            PolicyEngine.FinancialExecutionUnavailablePolicy,
            PolicyEngine.CapabilityEnabledPolicy,
            PolicyEngine.AiProposerAllowedPolicy,
            PolicyEngine.IrreversibleRequiresApprovalPolicy,
            PolicyEngine.RiskTierWithinAutoExecutePolicy,
        ];

        Assert.Equal(expected, decision.EvaluatedPolicies);
    }

    /// <summary>
    /// A refusal records the rules up to and including the one that refused, and no further.
    /// </summary>
    [Fact]
    public void A_disabled_capability_records_the_rules_up_to_the_one_that_refused()
    {
        var decision = _engine.Evaluate(
            Proposal(),
            Context(
                KillSwitchState.Disengaged,
                CapabilityPolicy.Disabled(Capability.ReferenceDataManagement)),
            Now);

        string[] expected =
        [
            PolicyEngine.KillSwitchPolicy,
            PolicyEngine.CapabilityDefinedPolicy,
            PolicyEngine.AiMayNotAdministerSafetyPolicy,
            PolicyEngine.FinancialExecutionUnavailablePolicy,
            PolicyEngine.CapabilityEnabledPolicy,
        ];

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(expected, decision.EvaluatedPolicies);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static PolicyContext Context(KillSwitchState killSwitch, params CapabilityPolicy[] policies) =>
        PolicyContext.Create("Test", killSwitch, policies);

    private static CapabilityPolicy Permissive() =>
        CapabilityPolicy.Create(Capability.ReferenceDataManagement, enabled: true, RiskTier.Low);

    private static ProposedBy Agent() =>
        ProposedBy.AiAgent("agent.test", "1.0", "prompts/test", "1.0");

    private static ActionProposal Proposal(
        Capability capability = Capability.ReferenceDataManagement,
        ActionEconomics? economics = null,
        ProposedBy? proposedBy = null)
    {
        proposedBy ??= ProposedBy.Service("test", "1.0");

        var isAi = proposedBy.IsAi;

        return ActionProposal.Create(
            CorrelationId.New(),
            capability,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new ReasonTestParameters(),
            economics ?? ActionEconomics.NoFinancialEffect(),
            proposedBy,
            idempotencyKey: Guid.NewGuid().ToString("n"),
            Now,
            evidence: isAi ? [Domain.Evidence.ClaimId.New()] : null,
            confidence: isAi ? Confidence.Create(0.8m) : null);
    }

    private sealed record ReasonTestParameters : IActionParameters
    {
        public string Describe() => "reason test parameters";
    }
}
