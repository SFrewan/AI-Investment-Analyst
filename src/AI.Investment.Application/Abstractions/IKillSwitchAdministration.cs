using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Abstractions;

/// <summary>Engages the kill switch. There is deliberately no method that disengages it.</summary>
/// <remarks>
/// <para>
/// The read side is <see cref="IKillSwitch"/>, and it is backed by two independent mechanisms - an
/// environment variable and a database flag - because they fail differently. This is the write side
/// of the second one, and it is one-way.
/// </para>
/// <para>
/// <strong>Why there is no disengage.</strong> Engaging is the safe direction and must be possible
/// from anywhere, including from an API. Disengaging is the dangerous direction, and routing it
/// through the same seam could not work anyway: the policy engine denies every action while the
/// switch is engaged, so a disengage proposal would be refused by the very state it exists to
/// clear. The only implementation that would function is one that bypassed the policy gate, and a
/// bypass that turns the kill switch off is the last thing this platform should own. Disengaging
/// stays where Phase 5 put it - out of band, by whoever has database or environment access, which
/// is deliberately a different and smaller set of people than those who can reach the API.
/// </para>
/// <para>
/// Engaging is still proposed, policy-evaluated and audited like any other side effect. When the
/// switch is already engaged the policy engine denies the proposal, which is the correct outcome:
/// the thing the caller wanted is already true.
/// </para>
/// </remarks>
public interface IKillSwitchAdministration
{
    /// <summary>
    /// Records an engaged flag, globally or for one capability.
    /// </summary>
    /// <param name="capability">Null for the global switch.</param>
    /// <param name="reason">Why. Recorded on the flag and read by whoever finds it later.</param>
    Task EngageAsync(
        Capability? capability,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
