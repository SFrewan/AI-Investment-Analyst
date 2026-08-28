using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores escalations and counts the ones nobody answered.</summary>
public sealed class EfEscalationStore : IEscalationStore
{
    private readonly AppDbContext _dbContext;

    public EfEscalationStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(Escalation escalation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escalation);

        await _dbContext.Escalations.AddAsync(escalation, cancellationToken).ConfigureAwait(false);
    }

    public Task<Escalation?> FindAsync(Guid escalationId, CancellationToken cancellationToken = default) =>
        _dbContext.Escalations.FirstOrDefaultAsync(e => e.EscalationId == escalationId, cancellationToken);

    public async Task<IReadOnlyList<Escalation>> GetOutstandingAsync(
        CancellationToken cancellationToken = default) =>
        await _dbContext.Escalations
            .Where(e => e.ResolvedAtUtc == null)
            .OrderBy(e => e.ExpiresAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountUnhandledAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        _dbContext.Escalations.CountAsync(
            e => e.ResolvedAtUtc == null && e.ExpiresAtUtc <= nowUtc,
            cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
