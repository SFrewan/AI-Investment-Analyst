using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Actions;

/// <summary>Claims idempotency keys using the database's uniqueness guarantee.</summary>
/// <remarks>
/// <para>
/// The claim is an INSERT against a primary key, not a read followed by a write. Under
/// concurrency - which is the normal condition when retries happen - a check-then-insert races,
/// and two callers both conclude the key is free. Letting the database arbitrate means exactly
/// one INSERT succeeds and the loser sees a unique-violation, which is the answer it needed.
/// </para>
/// <para>
/// The identity-map check in <see cref="TryClaimAsync"/> does not weaken that. It answers only for
/// keys this very context has already claimed and is still tracking; every key it has not seen
/// still goes to the database to be decided.
/// </para>
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

        // A key already claimed through THIS context is still tracked by it, and EF Core refuses
        // to track a second instance carrying the same key: Add throws InvalidOperationException
        // - "another instance with the same key value for {'IdempotencyKey'} is already being
        // tracked" - before any SQL is sent, so the DbUpdateException handler below could never
        // see it. The second claim of a key therefore blew up instead of returning false.
        //
        // Answering from the identity map is not the check-then-insert race the remarks above
        // warn about. That race is between separate callers, each with its own context, and it is
        // still settled where it has to be: in the database. This asks only whether this context
        // made the claim itself, which is a question it can answer without asking anyone.
        if (_dbContext.ProcessedActions.Local.Any(
                claimed => string.Equals(claimed.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)))
        {
            return false;
        }

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
