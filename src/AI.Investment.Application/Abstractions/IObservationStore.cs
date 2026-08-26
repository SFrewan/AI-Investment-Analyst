using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores what the platform knows.</summary>
/// <remarks>
/// <para>
/// Append-only, and point-in-time by construction: an observation is what a source said at a
/// moment, so a later contradicting value is a new observation rather than an edit. A registry of
/// current values that overwrote itself could not answer "what did we believe in March?", which is
/// the question every backtest asks.
/// </para>
/// <para>
/// Reads filter on <c>PublishedAtUtc</c>, never on the period a value describes. Filtering on the
/// wrong one produces look-ahead bias, and it cannot be corrected afterwards because by then the
/// history has been read with the distinction discarded.
/// </para>
/// </remarks>
public interface IObservationStore
{
    Task RecordAsync(
        IReadOnlyList<Observation> observations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything known about a subject that was public at <paramref name="asAtUtc"/>.
    /// </summary>
    Task<IReadOnlyList<Observation>> ForSubjectAsync(
        IngestionSubject subject,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent value of one attribute that was public at <paramref name="asAtUtc"/>.
    /// </summary>
    Task<Observation?> LatestAsync(
        IngestionSubject subject,
        string attribute,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default);
}
