using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// Names one SEC filing document and hands it to the ingestion gateway. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every line here is request construction.</strong> It opens no connection, reads no
/// configuration, touches no archive and consults no authorisation - it builds an
/// <see cref="IngestionRequest"/> and calls the gateway, which then applies registration,
/// admission, capability, rate limiting, archival, the run ledger and the Action/Policy seam in
/// that order. A fetcher that did any of that itself would be a second acquisition path, and the
/// gates would apply to only one of them.
/// </para>
/// <para>
/// <strong>It cannot currently cause a request to leave.</strong> The registered <c>sec-edgar</c>
/// source does not declare <see cref="DataCategory.RegulatoryFilingDocuments"/>, so admission
/// refuses every one of these before a connector is reached. That refusal is recorded as an
/// ingestion run like any other, which is what makes "nothing was dispatched" an auditable fact
/// rather than an absence of evidence.
/// </para>
/// </remarks>
public sealed class SecFilingDocumentFetcher : IFilingDocumentFetcher
{
    /// <summary>The registered source that serves EDGAR. Never a vendor label.</summary>
    public static readonly SourceId Source = SecEdgarProvider.Id;

    private readonly IngestionGateway _gateway;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public SecFilingDocumentFetcher(
        IngestionGateway gateway,
        ICorrelationContext correlation,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public Task<IngestionRun> FetchAsync(
        string cik,
        string accessionNumber,
        string primaryDocument,
        CancellationToken cancellationToken = default)
    {
        // Validated here so a malformed target never becomes a request at all. The connector
        // checks again on the way out - two checks, because this one can be bypassed by a caller
        // building a request by hand and that one cannot.
        var subject = FilingDocumentSubject.Parse(
            $"{cik}{FilingDocumentSubject.Separator}{accessionNumber}{FilingDocumentSubject.Separator}{primaryDocument}");

        if (!subject.IsAccepted)
        {
            throw new ArgumentException(
                $"'{cik}|{accessionNumber}|{primaryDocument}' is not a filing document this "
                    + $"connector can address: {subject.Refusal}. It is refused rather than "
                    + "corrected, because every refusal names a boundary that a rewrite would cross.",
                nameof(primaryDocument));
        }

        var request = IngestionRequest.Create(
            Source,
            DataCategory.RegulatoryFilingDocuments,
            Region.UnitedStates,
            IngestionSubject.Create(FilingDocumentSubject.SubjectKind, subject.Subject!.ToIdentifier()),
            _correlation.Current,
            _clock.UtcNow);

        return _gateway.IngestAsync(request, cancellationToken);
    }
}
