using AI.Investment.Domain.Autonomy;
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
/// enum value, unattended action with no resolved grant - all deny. There is no branch anywhere
/// in this file that reaches Execute because something could not be determined.</item>
/// <item><strong>Ordered by severity.</strong> The strongest refusals are evaluated first, so a
/// later permissive rule can never override an earlier prohibition.</item>
/// <item><strong>Structural rules are not configurable.</strong> Rules 3, 4 and 5 below cannot be
/// switched off by any configuration value. A safety property that configuration can disable is
/// a safety property that will eventually be disabled.</item>
/// <item><strong>Autonomy narrows, never widens.</strong> Rule 10 can turn an Execute into a
/// RequireApproval or a Deny. There is no value of any autonomy grant that turns a refusal into a
/// permission, which is what makes a grant safe to be a database row.</item>
/// </list>
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

    /// <summary>
    /// An unattended proposal must arrive with a resolved autonomy grant. Structural. Phase 6.
    /// </summary>
    public const string AutonomyResolvedPolicy = "policy.autonomy-resolved@1";

    /// <summary>The resolved mode, applied as a ceiling on the outcome. Phase 6.</summary>
    public const string AutonomyCeilingPolicy = "policy.autonomy-ceiling@1";

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

        // ---- 5. STRUCTURAL: an unattended action must carry a resolved grant --------------
        // A proposal with a cycle identifier was produced by the operating loop, which means no
        // human is in the room. The autonomy resolution is what says how much such an action may
        // do, and its absence is not "no restriction" - it is "nobody has established what the
        // restriction is". Not configurable, and evaluated before the capability's own settings,
        // so that no combination of configuration lets an unattended action run unresolved.
        evaluated.Add(AutonomyResolvedPolicy);

        if (proposal.CycleId is not null && context.Autonomy is null)
        {
            return PolicyDecision.Deny(
                proposal,
                $"Proposal {proposal.ProposalId} belongs to operating cycle {proposal.CycleId} but " +
                "arrived with no resolved autonomy grant. An unattended action whose permitted " +
                "autonomy is unknown is refused rather than assumed.",
                evaluated,
                nowUtc);
        }

        // ---- 6. Capability enabled -------------------------------------------------------
        evaluated.Add(CapabilityEnabledPolicy);

        if (!policy.Enabled)
        {
            return PolicyDecision.Deny(
                proposal,
                $"Capability '{proposal.Capability}' is disabled in environment '{context.EnvironmentName}'.",
                evaluated,
                nowUtc);
        }

        // ---- 7. Is an AI proposer permitted here at all? ---------------------------------
        evaluated.Add(AiProposerAllowedPolicy);

        if (proposal.ProposedBy.IsAi && !policy.AllowAiProposers)
        {
            return PolicyDecision.Deny(
                proposal,
                $"An AI proposer is not permitted for capability '{proposal.Capability}'.",
                evaluated,
                nowUtc);
        }

        // ---- 8. Irreversible actions need a human ----------------------------------------
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

        // ---- 9. Risk tier within the auto-execute ceiling --------------------------------
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

        // ---- 10. Autonomy ceiling --------------------------------------------------------
        // Everything above has said yes. This is the last question: is anybody watching, and if
        // not, has a human granted this capability the right to proceed without them?
        evaluated.Add(AutonomyCeilingPolicy);

        return ApplyAutonomyCeiling(proposal, context, policy, evaluated, nowUtc);
    }

    /// <summary>
    /// Applies the resolved autonomy mode as a ceiling on an otherwise-permitted action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrowing only. Every branch here returns Execute, RequireApproval or Deny, and the Execute
    /// branch is reachable only from a mode a human explicitly granted. There is no value of
    /// <see cref="AutonomyResolution"/> that turns any earlier refusal into a permission, because
    /// this method is never reached from one.
    /// </para>
    /// <para>
    /// "No grant" splits by capability, following the rule that resolution failures are
    /// <em>RequireApproval at minimum, Deny on the execution path</em>. An ungranted research action
    /// reaching a human is the system working; an ungranted order reaching a human is an approval
    /// queue being used to launder a permission nobody issued.
    /// </para>
    /// </remarks>
    private static PolicyDecision ApplyAutonomyCeiling(
        ActionProposal proposal,
        PolicyContext context,
        CapabilityPolicy policy,
        List<string> evaluated,
        DateTime nowUtc)
    {
        var autonomy = context.Autonomy;

        if (autonomy is null)
        {
            // Attended. A human or a request initiated this, so the question this rule asks -
            // "may it proceed with nobody watching?" - does not arise.
            return PolicyDecision.Execute(
                proposal,
                $"Risk tier {proposal.RiskTier} is within the unattended ceiling of " +
                $"{policy.MaxAutoExecuteRiskTier} for capability '{proposal.Capability}', and no prohibition applies.",
                evaluated,
                nowUtc);
        }

        switch (autonomy.Mode)
        {
            case AutonomyMode.AutoExecuteBounded:
            case AutonomyMode.ContinuousBounded:
                return PolicyDecision.Execute(
                    proposal,
                    $"Autonomy resolves to {autonomy.Mode} for capability '{proposal.Capability}', " +
                    $"which permits unattended execution. {autonomy.Reason}",
                    evaluated,
                    nowUtc);

            case AutonomyMode.ResearchOnly:
            case AutonomyMode.Advise:
            case AutonomyMode.PrepareForApproval:
                return PolicyDecision.RequireApproval(
                    proposal,
                    $"Autonomy resolves to {autonomy.Mode} for capability '{proposal.Capability}', " +
                    $"which prepares the action but does not execute it. {autonomy.Reason}",
                    evaluated,
                    nowUtc);

            case AutonomyMode.Unknown:
            case AutonomyMode.Off:
                return IsExecutionCapability(proposal.Capability)
                    ? PolicyDecision.Deny(
                        proposal,
                        $"Autonomy resolves to {autonomy.Mode} for capability '{proposal.Capability}'. " +
                        "On the execution path an unresolved grant denies rather than escalating: an " +
                        $"approval queue must not be used to obtain a permission nobody granted. {autonomy.Reason}",
                        evaluated,
                        nowUtc)
                    : PolicyDecision.RequireApproval(
                        proposal,
                        $"Autonomy resolves to {autonomy.Mode} for capability '{proposal.Capability}', " +
                        $"so the action escalates to a human instead of proceeding. {autonomy.Reason}",
                        evaluated,
                        nowUtc);

            default:
                // Unreachable today. Present so that adding a mode without updating this switch
                // denies rather than falling through to execution.
                return PolicyDecision.Deny(
                    proposal,
                    $"Autonomy mode '{autonomy.Mode}' is not recognised by this build, which is " +
                    "treated as no autonomy at all.",
                    evaluated,
                    nowUtc);
        }
    }

    /// <summary>
    /// Capabilities that govern the safety system itself. An AI proposer is refused these
    /// unconditionally.
    /// </summary>
    private static bool IsSafetyAdministration(Capability capability) =>
        capability is Capability.PolicyAdministration
            or Capability.AutonomyAdministration
            or Capability.ApprovalAdministration;

    /// <summary>
    /// Capabilities that place orders. An unresolved grant denies these rather than escalating.
    /// </summary>
    private static bool IsExecutionCapability(Capability capability) =>
        capability is Capability.FinancialExecution or Capability.SimulatedExecution;
}
