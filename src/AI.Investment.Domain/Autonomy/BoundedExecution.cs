using AI.Investment.Domain.Enums;

namespace AI.Investment.Domain.Autonomy;

/// <summary>Why an action may not be performed unattended, however the grant reads.</summary>
/// <remarks>
/// <see cref="None"/> is zero and is the only value that permits anything. A default-initialised
/// verdict refuses.
/// </remarks>
public enum BoundedExecutionRefusal
{
    /// <summary>Within the bounded class. The only non-refusal.</summary>
    None = 0,

    /// <summary>The capability may never run unattended, whatever a grant says.</summary>
    CapabilityExcluded = 1,

    /// <summary>The action cannot be undone, so nobody can correct it after the fact.</summary>
    NotReversible = 2,

    /// <summary>The action can be undone, but not for free. Not the lowest-risk class.</summary>
    ReversibleOnlyAtACost = 3,

    /// <summary>Above the lowest risk tier.</summary>
    RiskTierTooHigh = 4,

    /// <summary>The mode is not a value this rule knows how to judge.</summary>
    ModeNotRecognised = 5,
}

/// <summary>
/// Which action classes may run without anybody watching: the lowest-risk, reversible ones only.
/// </summary>
/// <remarks>
/// <para>
/// The canonical scope for bounded autonomy is "automatic execution of the lowest-risk, reversible
/// action classes", and this is that sentence written down as a total function. It is deliberately
/// narrower than the policy engine's existing irreversibility rule: that one stops an irreversible
/// action from executing without approval, while this one additionally refuses an action that is
/// reversible <em>at a cost</em>. An unwinding that costs money is a decision somebody should be
/// making, and the whole premise of unattended execution is that nobody is.
/// </para>
/// <para>
/// <strong>The rule is about the action, not about the grant.</strong> A grant says what a human was
/// willing to permit; this says what the class of action allows regardless. Both must agree, and
/// they are kept apart so that widening one cannot quietly widen the other.
/// </para>
/// <para>
/// Enforced at every point where unattended authority can be created: a
/// <see cref="PromotionWarrant"/> may not be issued above the tier this rule admits, and a grant
/// may not exceed its warrant. It is not wired into the dispatch path in this phase, because no
/// warrant can exist to reach that path - a point the phase documentation states plainly rather
/// than leaving to be discovered.
/// </para>
/// </remarks>
public static class BoundedExecutionRule
{
    /// <summary>The highest risk tier that may ever run unattended.</summary>
    public static RiskTier MaximumRiskTier => RiskTier.Low;

    /// <summary>The only reversibility class that may ever run unattended.</summary>
    public static ReversibilityClass RequiredReversibility => ReversibilityClass.Reversible;

    /// <summary>
    /// Whether one action class may be performed unattended at a given mode.
    /// </summary>
    /// <remarks>
    /// A mode at or below <see cref="AutonomyMode.PrepareForApproval"/> is not unattended execution,
    /// so this rule has no opinion about it and returns <see cref="BoundedExecutionRefusal.None"/>.
    /// The caller decides whether to ask.
    /// </remarks>
    public static BoundedExecutionRefusal Admits(
        Capability capability,
        ReversibilityClass reversibility,
        RiskTier riskTier,
        AutonomyMode mode)
    {
        if (!Enum.IsDefined(mode) || mode == AutonomyMode.Unknown)
        {
            return BoundedExecutionRefusal.ModeNotRecognised;
        }

        if (mode <= AutonomyMode.PrepareForApproval)
        {
            // Attended, or advisory. Somebody is looking, so the class restriction does not apply.
            return BoundedExecutionRefusal.None;
        }

        if (capability == Capability.FinancialExecution || AutonomyGrant.IsSafetyAdministration(capability))
        {
            return BoundedExecutionRefusal.CapabilityExcluded;
        }

        if (!Enum.IsDefined(reversibility) || !Enum.IsDefined(riskTier))
        {
            return BoundedExecutionRefusal.ModeNotRecognised;
        }

        if (reversibility == ReversibilityClass.Irreversible)
        {
            return BoundedExecutionRefusal.NotReversible;
        }

        if (reversibility != RequiredReversibility)
        {
            return BoundedExecutionRefusal.ReversibleOnlyAtACost;
        }

        return riskTier > MaximumRiskTier
            ? BoundedExecutionRefusal.RiskTierTooHigh
            : BoundedExecutionRefusal.None;
    }

    /// <summary>The refusal in words, for an audit record or an escalation.</summary>
    public static string Explain(BoundedExecutionRefusal refusal) => refusal switch
    {
        BoundedExecutionRefusal.None =>
            "within the lowest-risk, reversible class that may run unattended.",

        BoundedExecutionRefusal.CapabilityExcluded =>
            "this capability may never run unattended. Financial execution has no execution plane, " +
            "and a capability that administers the safety system could widen its own permission.",

        BoundedExecutionRefusal.NotReversible =>
            "the action cannot be undone. Unattended execution assumes a mistake can be corrected " +
            "before anybody notices it, and an irreversible one cannot.",

        BoundedExecutionRefusal.ReversibleOnlyAtACost =>
            "the action can be undone, but not for free. An unwinding that costs money is a decision " +
            "somebody should be making, and unattended means nobody is.",

        BoundedExecutionRefusal.RiskTierTooHigh =>
            $"the action is above risk tier {MaximumRiskTier}, which is the highest that may run " +
            "unattended.",

        BoundedExecutionRefusal.ModeNotRecognised =>
            "the autonomy mode, reversibility or risk tier is not a value this build can judge, so " +
            "nothing is permitted.",

        _ =>
            "the action was not judged, so nothing is permitted.",
    };
}
