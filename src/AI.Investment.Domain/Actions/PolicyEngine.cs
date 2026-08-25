using AI.Investment.Domain.Enums;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// The deterministic safety gate. Pure, total, fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place in the system where "may this happen?" is answered. It contains no
/// AI, performs no I/O, reads no clock and holds no state. Given the same proposal and context
/// it returns the same decision every time, which is what makes it exhaustively testable - and
/// testability is the only reason to believe a safety control works.
/// </para>
/// <para><strong>The properties that must never be lost:</strong></para>
/// <list type="number">
/// <item><strong>Total.</strong> Every path returns Execute, RequireApproval or Deny. There is
/// no fourth outcome and no "unknown".</item>
/// <item><strong>Fail-closed.</strong> Missing policy, unreadable kill switch, unrecognised
/// enum value - all deny. There is no branch anywhere in this file that reaches Execute because
/// something could not be determined.</item>
/// <item><strong>Ordered by severity.</strong> The strongest refusals are evaluated first, so a
/// later permissive rule can never override an earlier prohibition.</item>
/// <item><strong>Structural rules are not configurable.</strong> Rules 3 and 4 below cannot be
/// switched off by any configuration value. A safety property that configuration can disable is
/// a safety property that will eventually be disabled.</item>
/// </list>
/// <para>
/// Deliberately absent in Phase 1: capital limits, daily loss limits, position sizing,
/// concentration limits, cooldowns and autonomy grants with expiry. Those belong to the limit
/// engine and to continuous operation. What is here is the seam they attach to - each is an
/// additional rule in this ordered sequence, not a change to its shape.
/// </para>
/// </remarks>
public sealed class PolicyEngine : IPolicyEngine
{
    // Policy identifiers recorded on every decision so the audit trail says which rule fired.
    // Versioned, because a rule change that is invisible in the audit trail makes historical
    // decisions impossible to interpret.
    public const string KillSwitchPolicy = "policy.kill-switch@1";
    public const string CapabilityDefinedPolicy = "policy.capability-defined@1";
    public const string CapabilityEnabledPolicy = "policy.capability-enabled@1";
    public const string AiMayNotAdministerSafetyPolicy = "policy.ai-may-not-administer-safety@1";
    public const string FinancialExecutionUnavailablePolicy = "policy.financial-execution-unavailable@1";
    public const string AiProposerAllowedPolicy = "policy.ai-proposer-allowed@1";
    public const string IrreversibleRequiresApprovalPolicy = "policy.irreversible-requires-approval@1";
    public const string RiskTierWithinAutoExecutePolicy = "policy.risk-tier-within-auto-execute@1";

    public PolicyDecision Evaluate(ActionProposal proposal, PolicyContext context, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        var evaluated = new List<string>();

        // ---- 1. Kill switch -------------------------------------------------------------
        // Unknown is treated exactly like Engaged. If the system cannot determine whether it has
        // been told to stop, it stops. A control that fails open is not a control.
        evaluated.Add(KillSwitchPolicy);

        if (context.KillSwitch != KillSwitchState.Disengaged)
        {
            return PolicyDecision.Deny(
                proposal,
                context.KillSwitch == KillSwitchState.Engaged
                    ? "The kill switch is engaged. No action executes."
                    : "The kill switch state could not be determined, which is treated as engaged.",
                evaluated,
                nowUtc);
        }

        // ---- 2. A policy must exist for this capability ----------------------------------
        // No policy means no permission. Absence is not consent.
        evaluated.Add(CapabilityDefinedPolicy);

        if (!context.TryGetPolicy(proposal.Capability, out var policy) || policy is null)
        {
            return PolicyDecision.Deny(
                proposal,
                $"No policy is defined for capability '{proposal.Capability}' in environment " +
                $"'{context.EnvironmentName}'. An undefined capability is denied.",
                evaluated,
                nowUtc);
        }

        // ---- 3. STRUCTURAL: an AI agent may never administer the safety system ------------
        // Not configurable, and evaluated before the capability's own AI setting, so that no
        // combination of configuration can permit it. An agent that can change policy or
        // autonomy can grant itself anything; this is the rule that makes every other rule
        // meaningful.
        evaluated.Add(AiMayNotAdministerSafetyPolicy);

        if (proposal.ProposedBy.IsAi && IsSafetyAdministration(proposal.Capability))
        {
            return PolicyDecision.Deny(
                proposal,
                $"An AI proposer may never administer the safety system (capability " +
                $"'{proposal.Capability}'). This prohibition is structural and cannot be configured away.",
                evaluated,
                nowUtc);
        }

        // ---- 4. STRUCTURAL: no financial execution exists yet -----------------------------
        // There is no execution plane, no venue and no credential in this system. Until that
        // changes behind its own explicit gate, every such proposal is refused here rather than
        // relying on the absence of an implementation.
        evaluated.Add(FinancialExecutionUnavailablePolicy);

        if (proposal.Capability == Capability.FinancialExecution)
        {
            return PolicyDecision.Deny(
                proposal,
                "Financial execution is not available. No execution plane exists, and this refusal is " +
                "structural rather than a consequence of configuration.",
                evaluated,
                nowUtc);
        }

        // ---- 5. Capability enabled -------------------------------------------------------
        evaluated.Add(CapabilityEnabledPolicy);

        if (!policy.Enabled)
        {
            return PolicyDecision.Deny(
                proposal,
                $"Capability '{proposal.Capability}' is disabled in environment '{context.EnvironmentName}'.",
                evaluated,
                nowUtc);
        }

        // ---- 6. Is an AI proposer permitted here at all? ---------------------------------
        evaluated.Add(AiProposerAllowedPolicy);

        if (proposal.ProposedBy.IsAi && !policy.AllowAiProposers)
        {
            return PolicyDecision.Deny(
                proposal,
                $"An AI proposer is not permitted for capability '{proposal.Capability}'.",
                evaluated,
                nowUtc);
        }

        // ---- 7. Irreversible actions need a human ----------------------------------------
        // Reversibility, not amount, is the axis that matters. A cheap irreversible action gets
        // more scrutiny than an expensive reversible one.
        evaluated.Add(IrreversibleRequiresApprovalPolicy);

        if (proposal.Economics.Reversibility == ReversibilityClass.Irreversible &&
            !policy.AllowIrreversibleAutoExecute)
        {
            return PolicyDecision.RequireApproval(
                proposal,
                "The action is irreversible and unattended execution of irreversible actions is not " +
                $"permitted for capability '{proposal.Capability}'.",
                evaluated,
                nowUtc);
        }

        // ---- 8. Risk tier within the auto-execute ceiling --------------------------------
        evaluated.Add(RiskTierWithinAutoExecutePolicy);

        if (proposal.RiskTier > policy.MaxAutoExecuteRiskTier)
        {
            return PolicyDecision.RequireApproval(
                proposal,
                $"Risk tier {proposal.RiskTier} exceeds the unattended ceiling of " +
                $"{policy.MaxAutoExecuteRiskTier} for capability '{proposal.Capability}'.",
                evaluated,
                nowUtc);
        }

        // ---- Permitted --------------------------------------------------------------------
        return PolicyDecision.Execute(
            proposal,
            $"Risk tier {proposal.RiskTier} is within the unattended ceiling of " +
            $"{policy.MaxAutoExecuteRiskTier} for capability '{proposal.Capability}', and no prohibition applies.",
            evaluated,
            nowUtc);
    }

    /// <summary>
    /// Capabilities that govern the safety system itself. An AI proposer is refused these
    /// unconditionally.
    /// </summary>
    private static bool IsSafetyAdministration(Capability capability) =>
        capability is Capability.PolicyAdministration
            or Capability.AutonomyAdministration
            or Capability.ApprovalAdministration;
}
