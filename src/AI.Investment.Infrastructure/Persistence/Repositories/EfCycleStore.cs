using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores and finds operating cycles.</summary>
/// <remarks>
/// <para>
/// <see cref="TryAddAsync"/> lets the database arbitrate duplicates. The unique index on the trigger
/// key is the whole deduplication mechanism, and a unique violation is the expected answer rather
/// than an error: it means another worker - or an earlier delivery of the same observation - already
/// started this cycle. Reading first and inserting second would race precisely when a storm is
/// delivering the same observation to several workers at once.
/// </para>
/// <para>
/// The identity-map check mirrors the idempotency store's, and for the same reason: EF refuses to
/// track two instances with the same key before any SQL is sent, so a duplicate created twice on one
/// context would throw where the unique-violation handler could never see it.
/// </para>
/// </remarks>
public sealed class EfCycleStore : ICycleStore
{
    private readonly AppDbContext _dbContext;

    public EfCycleStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<bool> TryAddAsync(OperatingCycle cycle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (_dbContext.OperatingCycles.Local.Any(tracked =>
                string.Equals(tracked.TriggerKey, cycle.TriggerKey, StringComparison.Ordinal)))
        {
            return false;
        }

        await _dbContext.OperatingCycles.AddAsync(cycle, cancellationToken).ConfigureAwait(false);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (DbUpdateException)
        {
            // Somebody claimed this trigger key first. Detach so the failed insert does not poison
            // the next save on this context.
            _dbContext.Entry(cycle).State = EntityState.Detached;

            return false;
        }
    }

    public Task<OperatingCycle?> FindAsync(Guid cycleId, CancellationToken cancellationToken = default) =>
        _dbContext.OperatingCycles.FirstOrDefaultAsync(c => c.CycleId == cycleId, cancellationToken);

    public Task<OperatingCycle?> FindByTriggerKeyAsync(
        string triggerKey,
        CancellationToken cancellationToken = default) =>
        _dbContext.OperatingCycles.FirstOrDefaultAsync(c => c.TriggerKey == triggerKey, cancellationToken);

    public async Task<IReadOnlyList<OperatingCycle>> GetRunnableAsync(
        int limit,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            return [];
        }

        // Oldest first, so a cycle cannot starve behind newer work; leased cycles are excluded
        // unless their lease has expired, which is how a crashed worker's cycle comes back.
        return await _dbContext.OperatingCycles
            .Where(c => c.Status == CycleStatus.Running)
            .Where(c => c.LeaseExpiresAtUtc == null || c.LeaseExpiresAtUtc <= nowUtc)
            .OrderBy(c => c.UpdatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountRunningAsync(CancellationToken cancellationToken = default) =>
        _dbContext.OperatingCycles.CountAsync(c => c.Status == CycleStatus.Running, cancellationToken);

    public Task<int> CountRunningAsync(Capability capability, CancellationToken cancellationToken = default) =>
        _dbContext.OperatingCycles.CountAsync(
            c => c.Status == CycleStatus.Running && c.Capability == capability,
            cancellationToken);

    public Task<int> CountStartedByWatchAsync(
        Guid watchId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default) =>
        _dbContext.OperatingCycles.CountAsync(
            c => c.WatchId == watchId && c.StartedAtUtc >= sinceUtc,
            cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
