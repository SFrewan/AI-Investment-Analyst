using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Watching;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Reads and stages watches.</summary>
/// <remarks>
/// <see cref="SaveAsync"/> commits a watch's record of having fired, which the write guard permits
/// without an authorisation window - a firing is the watch's own bookkeeping. Everything else about
/// a watch is ordinary domain state and requires the seam, including creating one: a standing
/// instruction to spend money is exactly the kind of thing the gate exists for.
/// </remarks>
public sealed class EfWatchStore : IWatchStore
{
    private readonly AppDbContext _dbContext;

    public EfWatchStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<Watch>> GetEnabledAsync(
        TriggerType triggerType,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Watches
            .Where(watch => watch.TriggerType == triggerType)
            .Where(watch => watch.Enabled)
            .OrderByDescending(watch => watch.Priority)
            .ThenBy(watch => watch.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Watch>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Watches
            .OrderBy(watch => watch.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<Watch?> FindAsync(Guid watchId, CancellationToken cancellationToken = default) =>
        _dbContext.Watches.FirstOrDefaultAsync(watch => watch.WatchId == watchId, cancellationToken);

    public async Task AddAsync(Watch watch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watch);

        await _dbContext.Watches.AddAsync(watch, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
