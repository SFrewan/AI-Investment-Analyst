using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Acquires one filing document, identified the way the held filing pointers identify it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It decides nothing.</strong> Whether a fetch is allowed is <c>SourceAdmission</c>'s
/// answer, whether it is possible is <c>ProviderCapabilityCheck</c>'s, how fast is the rate
/// limiter's, and what happens to the bytes is the archive's. This exists so a caller can name a
/// filing document without assembling an ingestion request by hand, and for no other reason.
/// </para>
/// <para>
/// <strong>It does not reach the network, the archive or the authorisation seam.</strong> Every one
/// of those is reached through <c>IngestionGateway</c>, which is the only thing this calls. A
/// fetcher that talked to an <c>HttpClient</c> directly would bypass the gates in the order that
/// matters - and the gates are the reason a filing document cannot currently be fetched at all.
/// </para>
/// <para>
/// <strong>The identity is the filing, never a URL.</strong> A caller supplies CIK, accession
/// number and primary document; the path is derived inside the connector. Accepting a URL would
/// make the archived bytes' provenance a string somebody typed.
/// </para>
/// </remarks>
public interface IFilingDocumentFetcher
{
    /// <summary>
    /// Requests one filing document and returns the run that records what happened.
    /// </summary>
    /// <remarks>
    /// Returns an <see cref="IngestionRun"/> rather than bytes, and deliberately: a refusal is as
    /// real an outcome as a payload, and a signature returning bytes would have to represent
    /// "refused before the wire" as an absence. The run says which, and says it the same way every
    /// other acquisition in this platform does.
    /// </remarks>
    /// <param name="cik">The filer's CIK, as the filing pointer states it.</param>
    /// <param name="accessionNumber">EDGAR's identity for the filing, dashed form.</param>
    /// <param name="primaryDocument">The document's own filename, as the index states it.</param>
    Task<IngestionRun> FetchAsync(
        string cik,
        string accessionNumber,
        string primaryDocument,
        CancellationToken cancellationToken = default);
}
