using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Shadow;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores shadow measurements and counts them.</summary>
/// <remarks>
/// There is no update and no delete on this store, and the write guard refuses both at the context
/// as well. Two mechanisms rather than one, because this is the data a promotion to a higher
/// autonomy level would be argued from.
/// </remarks>
public sealed class EfShadowDecisionStore : IShadowDecisionStore
{
    private readonly AppDbContext _dbContext;

    public EfShadowDecisionStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(ShadowDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        await _dbContext.ShadowDecisions.AddAsync(decision, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CountAsync(DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        _dbContext.ShadowDecisions.CountAsync(s => s.RecordedAtUtc >= sinceUtc, cancellationToken);

    public Task<int> CountWouldHaveExecutedAsync(
        DateTime sinceUtc,
        CancellationToken cancellationToken = default) =>
        _dbContext.ShadowDecisions.CountAsync(
            s => s.RecordedAtUtc >= sinceUtc &&
                s.ShadowOutcome == Domain.Enums.PolicyOutcome.Execute &&
                s.ActualOutcome != Domain.Enums.PolicyOutcome.Execute,
            cancellationToken);

    public async Task<IReadOnlyList<ShadowDecision>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            return [];
        }

        return await _dbContext.ShadowDecisions
            .OrderByDescending(s => s.RecordedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
