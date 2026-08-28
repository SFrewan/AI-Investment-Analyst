using AI.Investment.Domain.Shadow;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores shadow measurements.</summary>
/// <remarks>
/// Append and read. There is no update and no delete, because a measurement that could be edited
/// after the fact is not a measurement - and this is the data a promotion to a higher autonomy
/// level would eventually be argued from.
/// </remarks>
public interface IShadowDecisionStore
{
    Task AddAsync(ShadowDecision decision, CancellationToken cancellationToken = default);

    Task<int> CountAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>How many measurements found that a higher level would have executed instead of asking.</summary>
    Task<int> CountWouldHaveExecutedAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShadowDecision>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
