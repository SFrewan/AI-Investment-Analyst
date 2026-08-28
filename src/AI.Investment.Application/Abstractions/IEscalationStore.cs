using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores escalations and answers the question the unattended criterion asks of them.</summary>
public interface IEscalationStore
{
    Task AddAsync(Escalation escalation, CancellationToken cancellationToken = default);

    Task<Escalation?> FindAsync(Guid escalationId, CancellationToken cancellationToken = default);

    /// <summary>Everything raised and not yet answered.</summary>
    Task<IReadOnlyList<Escalation>> GetOutstandingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many escalations reached their expiry without an answer.
    /// </summary>
    /// <remarks>
    /// The measurement behind "no unhandled escalation". Zero is the only acceptable value over an
    /// unattended run, and a number that climbs means the platform is asking questions nobody is
    /// answering - which ends with an operator who has stopped reading them.
    /// </remarks>
    Task<int> CountUnhandledAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
