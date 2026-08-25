using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Abstractions;

/// <summary>Records ingestion runs. Append-only.</summary>
/// <remarks>
/// <para>
/// The operational counterpart to provenance. A claim says which source a value came from; this
/// says whether that source has been answering, whether recent runs were complete, and which of
/// them the platform refused to make. Freshness monitoring and source reliability scoring both
/// read from here - the latter is how <see cref="DataSource.RecordReliability"/> stops being a
/// declaration and becomes a measurement.
/// </para>
/// <para>
/// <see cref="RecordAsync"/> takes a completed run. Runs are written once, at the end, like
/// <see cref="IActionExecutionStore"/> - so there is no update path to guard and no partially
/// written run to interpret.
/// </para>
/// </remarks>
public interface IIngestionRunStore
{
    /// <summary>Writes a completed run.</summary>
    Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent run for a source, whatever its outcome, or null if it has never run.
    /// </summary>
    /// <remarks>
    /// "Whatever its outcome" is deliberate. Freshness asks when the platform last <em>tried</em>,
    /// and a source that has been failing for a week is a different problem from one nobody has
    /// asked for - but both look identical if only successes are returned.
    /// </remarks>
    Task<IngestionRun?> GetLatestForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent successful or partially successful run for a source.</summary>
    Task<IngestionRun?> GetLatestSuccessfulForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a run with this request fingerprint has already completed successfully.
    /// </summary>
    /// <remarks>
    /// The read side of <see cref="IngestionRequest.Fingerprint"/>. Lets a scheduler skip work
    /// that has already been done rather than relying on the idempotency store to reject it after
    /// the fetch has happened.
    /// </remarks>
    Task<bool> HasCompletedAsync(string requestFingerprint, CancellationToken cancellationToken = default);

    /// <summary>Runs started within the window, newest first.</summary>
    Task<IReadOnlyList<IngestionRun>> GetRecentAsync(
        DateTime sinceUtc,
        int take,
        CancellationToken cancellationToken = default);
}
