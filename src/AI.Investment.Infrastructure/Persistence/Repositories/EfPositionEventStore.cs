using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Portfolio;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Appends position events, and reads them back. Nothing updates or deletes one.</summary>
/// <remarks>
/// <para>
/// <strong>Idempotency is the database's, not this class's.</strong> The obvious implementation -
/// query for the venue reference, and insert if absent - is wrong under concurrency: two callers
/// applying the same fill both find nothing and both insert. So the insert is attempted and the
/// unique-violation is caught. The race is then decided by the constraint, which is the only party
/// that can decide it.
/// </para>
/// <para>
/// The write goes through the guarded save rather than the seam's internal one. A fill moves money,
/// and a position event written without an authorised decision behind it is exactly what the
/// persistence guard exists to refuse - unlike an audit record, which must be written even when
/// nothing was authorised.
/// </para>
/// </remarks>
public sealed class EfPositionEventStore : IPositionEventStore
{
    /// <summary>PostgreSQL's unique-violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    private readonly AppDbContext _dbContext;

    public EfPositionEventStore(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<bool> AppendAsync(
        PositionEvent positionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionEvent);

        // A cheap read first, so the ordinary "already applied" case does not need an exception -
        // but never relied upon: the catch below is what makes it correct.
        var known = await _dbContext.PositionEvents
            .AsNoTracking()
            .AnyAsync(e => e.VenueReference == positionEvent.VenueReference, cancellationToken)
            .ConfigureAwait(false);

        if (known)
        {
            return false;
        }

        await _dbContext.PositionEvents.AddAsync(positionEvent, cancellationToken).ConfigureAwait(false);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Another caller won the race. Detach so the failed insert does not re-enter the next
            // save on this context, and report the fill as already applied.
            _dbContext.Entry(positionEvent).State = EntityState.Detached;

            return false;
        }

        return true;
    }

    public async Task<IReadOnlyList<PositionEvent>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await _dbContext.PositionEvents
            .AsNoTracking()
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.VenueReference)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PositionEvent>> ListForAsync(
        string instrument,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        var symbol = instrument.Trim();

        return await _dbContext.PositionEvents
            .AsNoTracking()
            .Where(e => e.Instrument == symbol)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.VenueReference)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal);
}
