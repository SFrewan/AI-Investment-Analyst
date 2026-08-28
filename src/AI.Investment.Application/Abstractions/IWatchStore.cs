using AI.Investment.Domain.Watching;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores and finds watches.</summary>
public interface IWatchStore
{
    /// <summary>Every enabled watch waiting for this kind of observation, highest priority first.</summary>
    Task<IReadOnlyList<Watch>> GetEnabledAsync(
        TriggerType triggerType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Watch>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Watch?> FindAsync(Guid watchId, CancellationToken cancellationToken = default);

    Task AddAsync(Watch watch, CancellationToken cancellationToken = default);

    /// <summary>Commits a watch's firing record. Not a domain write: it is the watch's own bookkeeping.</summary>
    Task SaveAsync(CancellationToken cancellationToken = default);
}
