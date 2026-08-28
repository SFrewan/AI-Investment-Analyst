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

    /// <summary>
    /// Every measurement recorded in a period, oldest first and unpaged.
    /// </summary>
    /// <remarks>
    /// Deliberately without a limit, and deliberately not reusing <see cref="GetRecentAsync"/>.
    /// Validation compares what a higher autonomy level would have done against what happened, and a
    /// comparison over the most recent N measurements is a comparison over a sample that selected
    /// itself by recency. Either the whole period is measured or the period is wrong.
    /// </remarks>
    Task<IReadOnlyList<ShadowDecision>> GetBetweenAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
