using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// Everything the policy engine is allowed to consider, gathered before evaluation.
/// </summary>
/// <remarks>
/// <para>
/// The context is assembled by the caller and passed in, rather than the engine reaching out to
/// read configuration or a database. That is what keeps <see cref="PolicyEngine"/> pure and
/// therefore exhaustively testable: given the same context and proposal it always returns the
/// same decision, with no I/O and no clock.
/// </para>
/// <para>
/// <see cref="FailClosed"/> is the value to use when the context cannot be determined. It leaves
/// the kill switch <see cref="KillSwitchState.Unknown"/> and defines no capabilities, so every
/// proposal is denied. A system that cannot tell whether it is allowed to act must not act.
/// </para>
/// <para>
/// <see cref="Autonomy"/> arrived with continuous operation and is deliberately nullable. Null does
/// not mean "no autonomy" - it means <em>the autonomy dimension does not apply</em>, because a human
/// or an HTTP request initiated this action and there is by definition somebody there. A cycle-driven
/// proposal is the opposite case, and the engine refuses one that reaches it with no resolution
/// attached: see <see cref="PolicyEngine.AutonomyResolvedPolicy"/>. That rule is what stops "null
/// means attended" being a hole an unattended path could fall through.
/// </para>
/// </remarks>
public sealed class PolicyContext
{
    public const int MaxEnvironmentNameLength = 60;

    private readonly Dictionary<Capability, CapabilityPolicy> _capabilities;

    private PolicyContext(
        string environmentName,
        KillSwitchState killSwitch,
        Dictionary<Capability, CapabilityPolicy> capabilities,
        AutonomyResolution? autonomy)
    {
        EnvironmentName = environmentName;
        KillSwitch = killSwitch;
        _capabilities = capabilities;
        Autonomy = autonomy;
    }

    /// <summary>
    /// The environment these policies apply to. A permission granted in Development carries no
    /// weight in Production, so the environment is part of the policy identity rather than an
    /// ambient assumption.
    /// </summary>
    public string EnvironmentName { get; }

    public KillSwitchState KillSwitch { get; }

    public IReadOnlyDictionary<Capability, CapabilityPolicy> Capabilities => _capabilities;

    /// <summary>
    /// The resolved autonomy for this action, when it is being taken unattended. Null when a human
    /// or a request initiated it.
    /// </summary>
    public AutonomyResolution? Autonomy { get; }

    public static PolicyContext Create(
        string environmentName,
        KillSwitchState killSwitch,
        IEnumerable<CapabilityPolicy> capabilities,
        AutonomyResolution? autonomy = null)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new DomainValidationException(nameof(environmentName), "An environment name is required.");
        }

        var trimmed = environmentName.Trim();

        if (trimmed.Length > MaxEnvironmentNameLength)
        {
            throw new DomainValidationException(
                nameof(environmentName),
                $"An environment name may not exceed {MaxEnvironmentNameLength} characters.");
        }

        if (!Enum.IsDefined(killSwitch))
        {
            // An unrecognised state is treated as Unknown, which denies. Fail closed.
            killSwitch = KillSwitchState.Unknown;
        }

        var map = new Dictionary<Capability, CapabilityPolicy>();

        foreach (var policy in capabilities ?? [])
        {
            // Last one wins rather than throwing: a duplicated entry is a configuration mistake,
            // and refusing to start is a harsher failure than it warrants. The resulting context
            // is still deterministic for a given input order.
            map[policy.Capability] = policy;
        }

        return new PolicyContext(trimmed, killSwitch, map, autonomy);
    }

    /// <summary>
    /// The context to use when policy could not be loaded. Denies everything.
    /// </summary>
    public static PolicyContext FailClosed(string environmentName = "unknown") =>
        Create(environmentName, KillSwitchState.Unknown, []);

    public bool TryGetPolicy(Capability capability, out CapabilityPolicy? policy)
    {
        if (_capabilities.TryGetValue(capability, out var found))
        {
            policy = found;
            return true;
        }

        policy = null;
        return false;
    }
}
