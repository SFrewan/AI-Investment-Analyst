using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Validation;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Validation;

/// <summary>
/// Reads the observation store as it stood at a moment in the past.
/// </summary>
/// <remarks>
/// <para>
/// Every query in this class narrows on <c>published_at_utc</c> and on nothing else that has to do
/// with time-of-knowledge. The column is indexed for exactly this, and the type deliberately exposes
/// no method that returns observations without a cutoff, so a caller cannot accidentally read the
/// present while believing it is reading the past.
/// </para>
/// <para>
/// <strong>Restatements resolve to what was known then.</strong> A figure published, then corrected,
/// appears twice: two rows describing the same instant with different publication times. Reading "as
/// of" a past decision must return the version that was current <em>then</em>, not the correction that
/// followed, so where several rows describe the same instant this takes the latest one that had been
/// published by the cutoff. Taking the newest row outright is the commonest way a bitemporal store is
/// misread, and it produces a backtest that quietly uses corrected data nobody had.
/// </para>
/// <para>
/// <c>retrieved_at_utc</c> appears nowhere in this file. An architecture test asserts that, because
/// the rule is easy to state, easy to agree with, and easy to break in a hurry when a query is a few
/// milliseconds slower than somebody wanted.
/// </para>
/// </remarks>
public sealed class EfValidationHistory : IValidationHistory
{
    private readonly AppDbContext _dbContext;

    public EfValidationHistory(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<Observation>> GetAdmissibleAsync(
        IngestionSubject subject,
        string attribute,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(cutoff);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        return await Admissible(subject, attribute, cutoff)
            .OrderBy(o => o.Provenance.AsOfUtc)
            .ThenBy(o => o.Provenance.PublishedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PricePoint>> GetPriceSeriesAsync(
        IngestionSubject subject,
        string attribute,
        DateTime fromUtc,
        DateTime toUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(cutoff);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        if (toUtc < fromUtc)
        {
            return [];
        }

        var rows = await Admissible(subject, attribute, cutoff)
            .Where(o => o.Value.Kind == ObservationValueKind.Number)
            .Where(o => o.Provenance.AsOfUtc >= fromUtc && o.Provenance.AsOfUtc <= toUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Latest(rows);
    }

    public async Task<PricePoint?> GetPriceAsOfAsync(
        IngestionSubject subject,
        string attribute,
        DateTime atUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(cutoff);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        var row = await Admissible(subject, attribute, cutoff)
            .Where(o => o.Value.Kind == ObservationValueKind.Number)
            .Where(o => o.Provenance.AsOfUtc <= atUtc)
            .OrderByDescending(o => o.Provenance.AsOfUtc)
            .ThenByDescending(o => o.Provenance.PublishedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToPricePoint(row);
    }

    public Task<int> CountUnreadableAsync(
        IngestionSubject subject,
        string attribute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        return _dbContext.Observations
            .Where(o => o.Subject.Kind == subject.Kind && o.Subject.Identifier == subject.Identifier)
            .Where(o => o.Attribute == attribute)
            .CountAsync(o => o.Value.Kind != ObservationValueKind.Number, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetSourceIdsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc < fromUtc)
        {
            return [];
        }

        var ids = await _dbContext.Observations
            .Where(o => o.Provenance.PublishedAtUtc >= fromUtc && o.Provenance.PublishedAtUtc <= toUtc)
            .Select(o => o.Provenance.SourceId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids.Select(id => id.Value).OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private IQueryable<Observation> Admissible(
        IngestionSubject subject,
        string attribute,
        KnowledgeCutoff cutoff) =>
        _dbContext.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == subject.Kind && o.Subject.Identifier == subject.Identifier)
            .Where(o => o.Attribute == attribute)

            // The one admission test. Publication, never retrieval.
            .Where(o => o.Provenance.PublishedAtUtc <= cutoff.AsOfUtc);

    /// <summary>
    /// One point per instant: the latest version of it that had been published by the cutoff.
    /// </summary>
    private static List<PricePoint> Latest(IReadOnlyList<Observation> rows) =>
        rows
            .GroupBy(o => o.Provenance.AsOfUtc)
            .Select(group => group
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .First())
            .OrderBy(o => o.Provenance.AsOfUtc)
            .Select(ToPricePoint)
            .ToList();

    private static PricePoint ToPricePoint(Observation observation) =>
        new(observation.Provenance.AsOfUtc, observation.Value.AsNumber(), observation.Provenance.PublishedAtUtc);
}
