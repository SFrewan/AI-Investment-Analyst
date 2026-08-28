using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores and finds operating cycles.</summary>
/// <remarks>
/// <para>
/// <see cref="AddAsync"/> is expected to fail when the trigger key is already taken, and callers
/// treat that as "somebody else already started this cycle" rather than as an error. Deduplicating
/// in the database rather than by reading first is the same choice the idempotency store makes, and
/// for the same reason: a read-then-write races exactly when it matters, which is when a storm is
/// delivering the same observation to several workers at once.
/// </para>
/// <para>
/// Cycles are saved by the store rather than deferred to a caller's unit of work. A cycle records
/// what the platform did, and the moment it most needs to be written down is the moment an action
/// was refused - when there is no unit of work to join.
/// </para>
/// </remarks>
public interface ICycleStore
{
    /// <summary>
    /// Persists a new cycle. Returns false when its trigger key is already claimed.
    /// </summary>
    Task<bool> TryAddAsync(OperatingCycle cycle, CancellationToken cancellationToken = default);

    Task<OperatingCycle?> FindAsync(Guid cycleId, CancellationToken cancellationToken = default);

    Task<OperatingCycle?> FindByTriggerKeyAsync(string triggerKey, CancellationToken cancellationToken = default);

    /// <summary>Cycles still running, oldest first, so nothing starves behind newer work.</summary>
    Task<IReadOnlyList<OperatingCycle>> GetRunnableAsync(
        int limit,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> CountRunningAsync(CancellationToken cancellationToken = default);

    Task<int> CountRunningAsync(Capability capability, CancellationToken cancellationToken = default);

    /// <summary>How many cycles a watch has started since <paramref name="sinceUtc"/>.</summary>
    Task<int> CountStartedByWatchAsync(
        Guid watchId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Commits pending changes to cycles. Saves the cycle's own progress, nothing else.</summary>
    Task SaveAsync(CancellationToken cancellationToken = default);
}
