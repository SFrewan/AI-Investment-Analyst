using System.ComponentModel.DataAnnotations;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// Configured inputs to the policy engine: the kill switch and the per-capability policies.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is the Phase 1 source of policy. It is deliberately not the long-term one:
/// autonomy grants become database records with expiry, measured quality metrics and automatic
/// demotion. What matters now is that policy comes from OUTSIDE the reasoning code and is read
/// by a deterministic provider, so that replacing this source later changes one class.
/// </para>
/// <para>
/// Note what is absent: there is no setting that disables the structural rules. An AI proposer
/// still cannot administer policy or autonomy, and financial execution is still refused,
/// whatever appears here.
/// </para>
/// </remarks>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>
    /// Environment variable that forces the kill switch on, checked before configuration.
    /// </summary>
    /// <remarks>
    /// An operator stopping a running system should not need a deployment. Any value other than
    /// "0" or "false" engages it - a typo engages the switch rather than disabling it.
    /// </remarks>
    public const string KillSwitchEnvironmentVariable = "AIINV_KILL_SWITCH";

    /// <summary>
    /// True stops all execution. Nullable on purpose: an absent value is
    /// <see cref="KillSwitchState.Unknown"/>, which denies. A missing setting must not read as
    /// permission.
    /// </summary>
    public bool? KillSwitchEngaged { get; init; }

    /// <summary>Per-capability policy. A capability absent from this list is denied.</summary>
    [Required]
    public IReadOnlyList<CapabilityPolicyOptions> Capabilities { get; init; } = [];
}

/// <summary>Configured policy for one capability.</summary>
public sealed class CapabilityPolicyOptions
{
    [Required]
    public Capability Capability { get; init; }

    public bool Enabled { get; init; }

    /// <summary>
    /// The highest risk tier permitted to execute unattended. Defaults to
    /// <see cref="RiskTier.Low"/> - the most restrictive value - so that an incompletely
    /// specified entry is conservative rather than permissive.
    /// </summary>
    public RiskTier MaxAutoExecuteRiskTier { get; init; } = RiskTier.Low;

    public bool AllowIrreversibleAutoExecute { get; init; }

    public bool AllowAiProposers { get; init; }
}
