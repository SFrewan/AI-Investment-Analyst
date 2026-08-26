using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores what the platform knows. Append-only.</summary>
/// <remarks>
/// <para>
/// Writes through the ordinary guarded save path, not the internal one. An observation is domain
/// state - something the platform believes - so recording one is a side effect that must pass
/// through the seam like any other. The normalisation pipeline opens that window before calling
/// here; if it ever stopped doing so, this store would start throwing rather than start writing
/// unaudited beliefs.
/// </para>
/// <para>
/// <strong>Every read filters on <c>published_at_utc</c>.</strong> Never on the period a value
/// describes, and never on retrieval time. A backtest that filtered on either would see figures
/// before the market did, and the resulting numbers look entirely plausible - which is what makes
/// look-ahead bias worth a guard rail in the only component able to enforce one.
/// </para>
/// <para>
/// Reads are untracked. An observation is written once and never revised - a later contradicting
/// value is a new row - so tracking would cost an identity-map entry per row and buy nothing.
/// </para>
/// </remarks>
public sealed class EfObservationStore : IObservationStore
{
    private readonly AppDbContext _dbContext;

    public EfObservationStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(
        IReadOnlyList<Observation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count == 0)
        {
            // Nothing to write, and nothing to commit. Saving an empty change set would still open
            // a transaction and still run the write guard, so a normaliser that found no readable
            // fields would look like an authorisation problem rather than an empty document.
            return;
        }

        await _dbContext.Observations
            .AddRangeAsync(observations, cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Observation>> ForSubjectAsync(
        IngestionSubject subject,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var query = ForSubject(_dbContext.Observations.AsNoTracking(), subject);

        return await query
            .Where(o => o.Provenance.PublishedAtUtc <= asAtUtc)
            .OrderBy(o => o.Attribute)
            .ThenByDescending(o => o.Provenance.PublishedAtUtc)
            .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Observation?> LatestAsync(
        IngestionSubject subject,
        string attribute,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        var trimmed = attribute.Trim();
        var query = ForSubject(_dbContext.Observations.AsNoTracking(), subject);

        return query
            .Where(o => o.Attribute == trimmed)
            .Where(o => o.Provenance.PublishedAtUtc <= asAtUtc)

            // Retrieval breaks a publication tie. Two observations published at the same instant
            // are the same claim seen twice, and the later retrieval is the one that reflects any
            // correction the source made in between.
            .OrderByDescending(o => o.Provenance.PublishedAtUtc)
            .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Narrows a query to one subject.</summary>
    /// <remarks>
    /// The null identifier is expressed separately rather than passed as a parameter. A sweep
    /// subject has no identifier, and SQL's <c>= NULL</c> is never true - so a single parameterised
    /// comparison would silently return nothing for exactly the subjects that have no identifier.
    /// </remarks>
    private static IQueryable<Observation> ForSubject(
        IQueryable<Observation> query,
        IngestionSubject subject)
    {
        var kind = subject.Kind;
        var identifier = subject.Identifier;

        var filtered = query.Where(o => o.Subject.Kind == kind);

        return identifier is null
            ? filtered.Where(o => o.Subject.Identifier == null)
            : filtered.Where(o => o.Subject.Identifier == identifier);
    }
}
