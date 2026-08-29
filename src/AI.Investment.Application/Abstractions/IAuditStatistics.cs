using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// How badly one capability's actions have been going, counted over a window.
/// </summary>
/// <param name="PolicyBreaches">
/// Actions the policy engine refused. A denial is the record of the platform proposing something it
/// was not permitted to do, which is what a breach is when nobody was watching.
/// </param>
/// <param name="ExecutionFailures">Authorised actions whose effect threw.</param>
public sealed record CapabilityIncidents(int PolicyBreaches, int ExecutionFailures);

/// <summary>
/// Counts, per capability, the two things the circuit breaker has to know.
/// </summary>
/// <remarks>
/// <para>
/// The breaker's demotion policy is fail-closed: a signal it cannot read demotes. That made it
/// correct and useless - policy breaches and execution failures were counted nowhere in the
/// platform, so every unattended grant demoted on the breaker's first sweep for want of a number
/// rather than because of one. This is the number.
/// </para>
/// <para>
/// <strong>It reads the audit trail rather than a second ledger.</strong> Every policy decision and
/// every execution already writes an audit record naming its capability, and that record is
/// append-only and written before the effect runs. A parallel counter would be a second account of
/// the same events, and the two would eventually disagree - at which point nobody could say which
/// one the breaker should have believed.
/// </para>
/// <para>
/// <strong>Read-only, and separate from <see cref="IAuditSink"/> on purpose.</strong> The sink has
/// no read method because reading the trail is a different concern from writing it; this is that
/// concern, narrowed to the two counts a safety control needs rather than opened up to arbitrary
/// queries over an append-only record.
/// </para>
/// <para>
/// An implementation that cannot answer should throw rather than return zeros. Zero means "nothing
/// went wrong", the caller turns a failure into "unknown", and unknown demotes - so a store that
/// guessed favourably on the way down would defeat the whole control.
/// </para>
/// </remarks>
public interface IAuditStatistics
{
    /// <summary>
    /// Denials and failures recorded for one capability between two instants, inclusive.
    /// </summary>
    Task<CapabilityIncidents> CountIncidentsAsync(
        Capability capability,
        DateTime sinceUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
