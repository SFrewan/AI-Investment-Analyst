using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Opportunities;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores opportunities.</summary>
/// <remarks>
/// <c>AddAsync</c> upserts by identity rather than failing on a second call, because the workflow
/// saves the same aggregate at each lifecycle step and a caller that has to remember whether an
/// opportunity is new is a caller that will eventually get it wrong.
/// </remarks>
public sealed class EfOpportunityRepository : IOpportunityRepository
{
    private readonly AppDbContext _dbContext;

    public EfOpportunityRepository(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(Opportunity opportunity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var tracked = _dbContext.Opportunities.Local
            .FirstOrDefault(candidate => candidate.OpportunityId == opportunity.OpportunityId);

        if (tracked is null)
        {
            var existing = await _dbContext.Opportunities
                .FirstOrDefaultAsync(
                    candidate => candidate.OpportunityId == opportunity.OpportunityId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await _dbContext.Opportunities.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);

                return;
            }
        }

        if (!ReferenceEquals(tracked, opportunity))
        {
            _dbContext.Entry(opportunity).State = EntityState.Modified;
        }
    }

    public Task<Opportunity?> GetAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Opportunities
            .FirstOrDefaultAsync(candidate => candidate.OpportunityId == opportunityId, cancellationToken);

    public async Task<IReadOnlyList<Opportunity>> ListAsync(
        OpportunityStatus status,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Opportunities
            .Where(candidate => candidate.Status == status)
            .OrderByDescending(candidate => candidate.StatusChangedAtUtc);

        return limit == int.MaxValue
            ? await query.ToListAsync(cancellationToken).ConfigureAwait(false)
            : await query.Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
