using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Runs an ingestion and then normalises whatever it archived.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Its whole job is ordering two operations and being honest about what
/// happened, and any judgement that appeared here would be judgement that had escaped the gateway
/// or the pipeline - the two places that can be tested without a network.
/// </para>
/// <para>
/// <strong>Normalisation is skipped when there is nothing to read.</strong> A refused run archived
/// nothing, and normalising it would produce a quarantine record for a payload that was never
/// fetched - inventing a data-quality problem out of a compliance decision.
/// </para>
/// <para>
/// <strong>A partial run is still normalised.</strong> It archived real bytes; that some pages were
/// missed is recorded on the run itself. Discarding what did arrive because more was expected would
/// throw away good data over an incomplete fetch.
/// </para>
/// <para>
/// <strong>A failure to normalise does not undo the ingestion.</strong> The run is in the ledger and
/// the bytes are in the archive before this is even called, which is the point of keeping the two
/// halves separate: normalisation can be fixed and re-run later against exactly the bytes that
/// defeated it. Losing the fetch as well would mean re-fetching, which costs somebody's rate limit
/// and may not return the same document.
/// </para>
/// </remarks>
public sealed class DataAcquisitionService : IDataAcquisition
{
    private readonly IIngestionGateway _ingestion;
    private readonly INormalizationPipeline _normalization;

    public DataAcquisitionService(
        IIngestionGateway ingestion,
        INormalizationPipeline normalization)
    {
        _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
        _normalization = normalization ?? throw new ArgumentNullException(nameof(normalization));
    }

    public async Task<AcquisitionResult> AcquireAsync(
        IngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = await _ingestion.IngestAsync(request, cancellationToken).ConfigureAwait(false);

        if (run.Artifacts.Count == 0)
        {
            // Refused, failed, or genuinely returned nothing. All three are already described on
            // the run, and null says normalisation was not attempted rather than attempted and
            // empty.
            return new AcquisitionResult(run, Normalization: null);
        }

        var summary = await _normalization
            .NormalizeAsync(run, cancellationToken)
            .ConfigureAwait(false);

        return new AcquisitionResult(run, summary);
    }
}
