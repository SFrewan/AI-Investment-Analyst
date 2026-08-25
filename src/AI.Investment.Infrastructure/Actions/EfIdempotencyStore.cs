using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Actions;

/// <summary>Claims idempotency keys using the database's uniqueness guarantee.</summary>
/// <remarks>
/// The claim is an INSERT against a primary key, not a read followed by a write. Under
/// concurrency - which is the normal condition when retries happen - a check-then-insert races,
/// and two callers both conclude the key is free. Letting the database arbitrate means exactly
/// one INSERT succeeds and the loser sees a unique-violation, which is the answer it needed.
/// </remarks>
public sealed class EfIdempotencyStore : IIdempotencyStore
{
    private readonly AppDbContext _dbContext;

    public EfIdempotencyStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<bool> TryClaimAsync(
        string idempotencyKey,
        Guid proposalId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var claim = new ProcessedAction(idempotencyKey, proposalId, nowUtc);

        await _dbContext.ProcessedActions.AddAsync(claim, cancellationToken).ConfigureAwait(false);

        try
        {
            await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Someone else claimed it first. Detach so the failed insert does not poison the
            // next SaveChanges on this context.
            _dbContext.Entry(claim).State = EntityState.Detached;
            return false;
        }
    }
}
