using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.UnitTests.Opportunities;

/// <summary>Hand-written doubles for the discovery and cycle-work tests.</summary>
/// <remarks>
/// No mocking framework, in keeping with the rest of the repository. The observation store below
/// reproduces the one behaviour of the real one that the discoverer depends on - a read admits only
/// what had been published by the instant asked for - because a double that admitted everything
/// would make a look-ahead defect in the discoverer invisible.
/// </remarks>
internal sealed class SeededObservationStore : IObservationStore
{
    private readonly List<Observation> _observations = [];

    public IReadOnlyList<Observation> All => _observations;

    public void Seed(IEnumerable<Observation> observations) => _observations.AddRange(observations);

    public Task RecordAsync(
        IReadOnlyList<Observation> observations,
        CancellationToken cancellationToken = default)
    {
        _observations.AddRange(observations);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Observation>> ForSubjectAsync(
        IngestionSubject subject,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Observation>>(
            _observations
                .Where(o => string.Equals(o.Subject.Kind, subject.Kind, StringComparison.Ordinal))
                .Where(o => string.Equals(o.Subject.Identifier, subject.Identifier, StringComparison.Ordinal))

                // The one admission test the real store applies. Publication, never retrieval.
                .Where(o => o.Provenance.PublishedAtUtc <= asAtUtc)
                .ToList());

    public Task<Observation?> LatestAsync(
        IngestionSubject subject,
        string attribute,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _observations
                .Where(o => string.Equals(o.Subject.Kind, subject.Kind, StringComparison.Ordinal))
                .Where(o => string.Equals(o.Subject.Identifier, subject.Identifier, StringComparison.Ordinal))
                .Where(o => string.Equals(o.Attribute, attribute, StringComparison.Ordinal))
                .Where(o => o.Provenance.PublishedAtUtc <= asAtUtc)
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .FirstOrDefault());
}

/// <summary>An opportunity repository with no database behind it.</summary>
internal sealed class RecordingOpportunityRepository : IOpportunityRepository
{
    private readonly List<Opportunity> _opportunities = [];

    public IReadOnlyList<Opportunity> All => _opportunities;

    public Task AddAsync(Opportunity opportunity, CancellationToken cancellationToken = default)
    {
        if (!_opportunities.Any(o => o.OpportunityId == opportunity.OpportunityId))
        {
            _opportunities.Add(opportunity);
        }

        return Task.CompletedTask;
    }

    public Task<Opportunity?> GetAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_opportunities.FirstOrDefault(o => o.OpportunityId == opportunityId));

    public Task<IReadOnlyList<Opportunity>> ListAsync(
        OpportunityStatus status,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Opportunity>>(
            _opportunities.Where(o => o.Status == status).Take(limit).ToList());
}

/// <summary>Builds stored closing prices the way the market-data normaliser would have.</summary>
internal static class MarketObservations
{
    public const string CloseAttribute = "security.close";

    public static readonly SourceId Source = SourceId.Create("operator-price-history");

    public static IngestionSubject Security(string ticker) => IngestionSubject.Create("Security", ticker);

    /// <summary>
    /// One close: a session, a price, and when it became public.
    /// </summary>
    /// <remarks>
    /// The publication time is a separate argument rather than derived from the session, because the
    /// gap between the two is the thing every point-in-time test in this file is about.
    /// </remarks>
    public static Observation Close(
        IngestionSubject subject,
        DateTime sessionCloseUtc,
        decimal close,
        DateTime? publishedAtUtc = null,
        DateTime? retrievedAtUtc = null)
    {
        var published = publishedAtUtc ?? sessionCloseUtc.AddMinutes(15);
        var retrieved = retrievedAtUtc ?? published.AddHours(1);

        return Observation.RecordFact(
            subject,
            CloseAttribute,
            ObservationValue.Number(close),
            Provenance.Create(Source, sessionCloseUtc, published, retrieved, subject.Identifier));
    }

    /// <summary>A whole series, one session per day, from a first session.</summary>
    public static List<Observation> Series(
        IngestionSubject subject,
        DateTime firstSessionUtc,
        IReadOnlyList<decimal> closes)
    {
        var observations = new List<Observation>(closes.Count);

        for (var i = 0; i < closes.Count; i++)
        {
            observations.Add(Close(subject, firstSessionUtc.AddDays(i), closes[i]));
        }

        return observations;
    }
}
