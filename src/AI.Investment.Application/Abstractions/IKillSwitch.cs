using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads the kill switch.</summary>
/// <remarks>
/// <para>
/// Backed by a database flag <em>and</em> an environment variable, and engaged if either says so.
/// Two independent mechanisms because they fail in different ways and are reachable by different
/// people: the variable stops a process that has already started misbehaving without needing the
/// database to be healthy, and the flag survives a restart.
/// </para>
/// <para>
/// <strong>Fail closed.</strong> An implementation that cannot determine the state returns
/// <see cref="KillSwitchState.Unknown"/>, which the policy engine already treats exactly like
/// <see cref="KillSwitchState.Engaged"/>. A switch of unknown state is a switch that is on.
/// </para>
/// </remarks>
public interface IKillSwitch
{
    /// <summary>The global state, or the state for one capability when it has its own.</summary>
    Task<KillSwitchState> ReadAsync(Capability? capability = null, CancellationToken cancellationToken = default);
}
