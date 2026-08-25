using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// What the system is permitted to do for one capability, in one environment.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, with no setters and no mutating methods. Policy is changed by supplying a
/// different <see cref="PolicyContext"/> at the boundary, never by an object reaching in and
/// altering one. Nothing inside the analysis pipeline - and specifically nothing an agent
/// produces - can obtain a reference to one of these and widen it.
/// </para>
/// <para>
/// This is the Phase 1 foundation of what becomes <c>AutonomyGrant</c>: the same lookup shape,
/// without expiry, exposure bands or automatic demotion. Those arrive with continuous
/// operation, and they extend this record rather than replacing it.
/// </para>
/// </remarks>
public sealed record CapabilityPolicy
{
    private CapabilityPolicy(
        Capability capability,
        bool enabled,
        RiskTier maxAutoExecuteRiskTier,
        bool allowIrreversibleAutoExecute,
        bool allowAiProposers)
    {
        Capability = capability;
        Enabled = enabled;
        MaxAutoExecuteRiskTier = maxAutoExecuteRiskTier;
        AllowIrreversibleAutoExecute = allowIrreversibleAutoExecute;
        AllowAiProposers = allowAiProposers;
    }

    public Capability Capability { get; }

    /// <summary>When false, every action in this capability is denied.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The highest risk tier that may execute without a human. Anything above requires approval.
    /// </summary>
    public RiskTier MaxAutoExecuteRiskTier { get; }

    /// <summary>
    /// Whether an irreversible action may execute unattended. Defaults to false everywhere, and
    /// should stay false for a long time: reversibility is the property that makes a mistake
    /// survivable.
    /// </summary>
    public bool AllowIrreversibleAutoExecute { get; }

    /// <summary>
    /// Whether an AI agent may propose actions in this capability at all. False by default.
    /// Note this permits an agent to PROPOSE, never to decide - the proposal still passes every
    /// other rule.
    /// </summary>
    public bool AllowAiProposers { get; }

    public static CapabilityPolicy Create(
        Capability capability,
        bool enabled,
        RiskTier maxAutoExecuteRiskTier,
        bool allowIrreversibleAutoExecute = false,
        bool allowAiProposers = false)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        if (!Enum.IsDefined(maxAutoExecuteRiskTier))
        {
            throw new DomainValidationException(
                nameof(maxAutoExecuteRiskTier),
                $"Unrecognised risk tier '{maxAutoExecuteRiskTier}'.");
        }

        return new CapabilityPolicy(
            capability,
            enabled,
            maxAutoExecuteRiskTier,
            allowIrreversibleAutoExecute,
            allowAiProposers);
    }

    /// <summary>A capability that is switched off entirely.</summary>
    public static CapabilityPolicy Disabled(Capability capability) =>
        Create(capability, enabled: false, RiskTier.Low);
}
