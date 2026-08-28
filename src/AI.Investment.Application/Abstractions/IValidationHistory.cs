using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Reads history as it stood at a moment in the past.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes a <see cref="KnowledgeCutoff"/> and every implementation filters on
/// <c>Provenance.PublishedAtUtc</c>. The cutoff is not a convenience parameter that an implementation
/// may ignore when it is inconvenient: it is the whole interface. A store that returned everything
/// and left the filtering to the caller would put the most consequential rule in this system in the
/// place most likely to forget it.
/// </para>
/// <para>
/// <strong>Retrieval time is not a permitted filter.</strong> An implementation that narrowed on
/// <c>RetrievedAtUtc</c> would make historical results depend on this installation's own fetch
/// history, so that backfilling a source silently changes the past. An architecture test asserts that
/// no implementation of this interface mentions it.
/// </para>
/// </remarks>
public interface IValidationHistory
{
    /// <summary>
    /// Observations of one attribute of one subject that were public at the cutoff, oldest first.
    /// </summary>
    Task<IReadOnlyList<Observation>> GetAdmissibleAsync(
        IngestionSubject subject,
        string attribute,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A price series over a period, containing only prices that were public at the cutoff.
    /// </summary>
    /// <remarks>
    /// Ordered by the instant each price describes, so that a series can be read end to end. Prices
    /// whose value cannot be read as a number are omitted and reported through
    /// <see cref="CountUnreadableAsync"/> rather than silently coerced to zero, because a zero price
    /// is not a cheap asset - it is missing data that will quietly dominate any return computed from
    /// it.
    /// </remarks>
    Task<IReadOnlyList<PricePoint>> GetPriceSeriesAsync(
        IngestionSubject subject,
        string attribute,
        DateTime fromUtc,
        DateTime toUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The last price at or before an instant that was public at the cutoff, or null if there is none.
    /// </summary>
    Task<PricePoint?> GetPriceAsOfAsync(
        IngestionSubject subject,
        string attribute,
        DateTime atUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>How many stored values for this attribute could not be read as numbers.</summary>
    Task<int> CountUnreadableAsync(
        IngestionSubject subject,
        string attribute,
        CancellationToken cancellationToken = default);

    /// <summary>The registered sources the evidence in a period came from.</summary>
    Task<IReadOnlyList<string>> GetSourceIdsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One prediction as it exists in the record, before the point-in-time guard has judged it.
/// </summary>
/// <remarks>
/// Separate from <see cref="PredictionRecord"/> on purpose. This is what the repository holds - which
/// may be incomplete, may have no admissibility evidence, and may be a prediction about the past.
/// <see cref="PredictionRecord"/> is what survives judgement. Keeping them apart is what lets a run
/// count what it refused instead of throwing on the first bad row and reporting nothing.
/// </remarks>
public sealed record PredictionCandidate(
    Guid PredictionId,
    IngestionSubject Subject,
    DateTime DecidedAtUtc,
    DateTime ResolvesAtUtc,
    PredictionDirection Direction,
    CalculationVersion Methodology,
    string SourceReference,
    DateTime? EvidenceAvailableAtUtc = null,
    Percentage? StatedProbability = null,
    Confidence? StatedConfidence = null,
    Guid? ProposalId = null);

/// <summary>Supplies the predictions a validation run measures.</summary>
/// <remarks>
/// No delete and no filter beyond the window. A hit rate computed over the predictions somebody chose
/// to keep is not a hit rate, and the surest way to avoid that is for the catalogue to have no opinion
/// about which predictions are interesting.
/// </remarks>
public interface IPredictionCatalogue
{
    Task<IReadOnlyList<PredictionCandidate>> GetAsync(
        EvaluationWindow window,
        CancellationToken cancellationToken = default);
}
