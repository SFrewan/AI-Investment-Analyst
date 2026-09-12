using System.Net.Http.Headers;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// Connector for the U.S. Securities and Exchange Commission's EDGAR system.
/// </summary>
/// <remarks>
/// <para>
/// The first connector, and chosen for what it is rather than what it is cheap: EDGAR is the
/// <em>originating record</em> for U.S. company disclosure. A vendor summarising a 10-K is a
/// report of a filing; this is the filing. It also happens to require no key, no account and no
/// payment, which is why it can be built and run without a commercial decision.
/// </para>
/// <para>
/// <strong>Compliance is built in, not bolted on.</strong> Every request identifies this
/// installation through a <c>User-Agent</c> carrying an application name and contact address, as
/// the SEC's fair-access policy requires; the connector declares a ten-per-second quota so the
/// gateway's rate limiter keeps to the published ceiling rather than discovering it by being
/// throttled; and the connector is not registered at all unless a contact address is configured.
/// </para>
/// <para>
/// It fetches bytes and nothing else. No parsing, no reshaping, no field extraction - that is
/// normalisation's job, and it happens after the archive has stored what the SEC actually
/// returned. If EDGAR changes a JSON shape, normalisation breaks visibly instead of history
/// quietly changing its account of what was filed.
/// </para>
/// </remarks>
public sealed class SecEdgarProvider : IDataProvider
{
    /// <summary>The registry key. Matches <see cref="SecEdgarSource"/>.</summary>
    public static readonly SourceId Id = SourceId.Create("sec-edgar");

    private readonly HttpClient _httpClient;
    private readonly SecEdgarOptions _options;
    private readonly IClock _clock;

    public SecEdgarProvider(
        HttpClient httpClient,
        IOptions<SecEdgarOptions> options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        Capabilities = BuildCapabilities(_options.MaxRequestsPerSecond);
    }

    public SourceId SourceId => Id;

    public ProviderCapabilities Capabilities { get; }

    public async Task<ProviderResponse> FetchAsync(
        IngestionRequest request,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Three request shapes, kept apart here rather than merged.
        //
        // Most categories name a company and resolve to a CIK path. A market-wide frame names a
        // PERIOD and has no CIK at all. A filing document names one document inside one filing and
        // needs three parts. Routing them through one identifier check would mean accepting one
        // where another belongs, and the failure would not be visible: a company path built from a
        // period subject returns somebody's accounts and would be archived as a cross-section of
        // the market, and a submissions index archived under a filing-document subject would be
        // read later as the document it merely lists.
        string path;
        string recordId;

        if (request.Category == DataCategory.RegulatoryFilingDocuments)
        {
            var subject = FilingDocumentSubject.Parse(request.Subject.Identifier);

            if (!subject.IsAccepted)
            {
                throw new InvalidOperationException(
                    $"A filing document is identified as 'cik|accessionNumber|primaryDocument', and " +
                    $"'{request.Subject}' was refused as {subject.Refusal}. Every refusal names a " +
                    "boundary the identifier crossed; none of them is a badly typed name that could " +
                    "be corrected here.");
            }

            path = SecEdgarEndpoints.FilingDocument(subject.Subject!);
            recordId = subject.Subject!.ToIdentifier();
        }
        else if (request.Category == DataCategory.MarketWideDisclosure)
        {
            var frame = SecEdgarEndpoints.ParseFrame(request.Subject.Identifier);

            if (frame is null)
            {
                throw new InvalidOperationException(
                    $"A market-wide frame is identified as 'taxonomy/concept/unit/period', and " +
                    $"'{request.Subject}' is not one. Segments are letters and digits only: an " +
                    "identifier carrying a separator or an escape is an attempt to reach a " +
                    "different endpoint, not a badly typed period.");
            }

            path = SecEdgarEndpoints.Frames(frame);
            recordId = frame.ToString();
        }
        else
        {
            var cik = SecEdgarEndpoints.NormaliseCik(request.Subject.Identifier);

            if (cik is null)
            {
                throw new InvalidOperationException(
                    $"EDGAR identifies companies by CIK, and '{request.Subject}' does not contain " +
                    "one. A ticker must be resolved to a CIK before ingestion.");
            }

            var companyPath = SecEdgarEndpoints.ForCategory(request.Category, cik);

            if (companyPath is null)
            {
                throw new InvalidOperationException(
                    $"EDGAR serves no endpoint for {request.Category}. The capability check should " +
                    "have refused this request before it reached the connector.");
            }

            path = companyPath;
            recordId = $"CIK{cik}";
        }

        // Filing documents live on a different EDGAR host from the JSON API, so this one category
        // is addressed absolutely and every other keeps resolving relatively against the client's
        // BaseAddress.
        //
        // An absolute request URI takes precedence over HttpClient.BaseAddress, which is what lets
        // one registered client reach both hosts without a second registration - same handler,
        // same timeout, same automatic decompression, same fair-access identity, same declared
        // quota, and the same rate limiter the gateway applies per source. The archive host is a
        // transport endpoint, not a second source: everything fetched from either is recorded
        // under sec-edgar.
        var isDocument = request.Category == DataCategory.RegulatoryFilingDocuments;

        var requestUri = isDocument
            ? new Uri(new Uri(_options.ArchiveBaseAddress, UriKind.Absolute), path)
            : new Uri(path, UriKind.Relative);

        using var message = new HttpRequestMessage(HttpMethod.Get, requestUri);

        // Required by the SEC's fair-access policy. Set per request rather than once on the
        // client so it cannot be silently lost by a client reconfigured elsewhere.
        //
        // Added WITHOUT validation, deliberately. The SEC documents the required form as
        // "Sample Company Name AdminContact@domain.com", and an e-mail address is not a valid
        // User-Agent product token: '@' is not in RFC 7230's token character set. ParseAdd
        // therefore throws FormatException on the exact value the SEC asks for, which meant this
        // connector could not complete a single request - every company failed identically, before
        // the wire, with a message that named the exception and not the cause. Sending what the
        // service asked for matters more than satisfying a parser stricter than the service.
        message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        // Accept-Encoding is deliberately NOT set here. The handler is configured for automatic
        // decompression, which adds the header itself and, unlike a hand-written one, also unwraps
        // the response. Setting it by hand asks for gzip that nothing decompresses.

        // What this endpoint actually returns. The JSON API is asked for JSON; a filing document
        // is HTML, XML or text, and asking that endpoint for JSON was part of the same mistake as
        // asking the wrong host - a request shaped for an API pointed at a document tree. Both
        // headers are per-category and neither changes what any other category sends.
        message.Headers.Accept.Add(isDocument
            ? new MediaTypeWithQualityHeaderValue("*/*")
            : new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        // Throws on any non-success status. An empty response and a failed request mean opposite
        // things to a ledger, and conflating them turns a failure into a silent gap.
        //
        // Wrapped so the status survives. EnsureSuccessStatusCode throws a bare
        // HttpRequestException; IngestionGateway.Describe records type names only, so the status
        // was reaching the ledger as "HttpRequestException during ingestion." and nothing more -
        // which is how five failed document fetches said only that something HTTP-shaped had gone
        // wrong. ProviderTransportException carries a closed-set token built from framework
        // enumerations, so the ledger gets "[status:404]" instead, and it derives from
        // HttpRequestException so every existing handler still catches it. This is the treatment
        // EodhdProvider already gives its transport failures; nothing new is introduced.
        if (!response.IsSuccessStatusCode)
        {
            HttpRequestException inner;

            try
            {
                response.EnsureSuccessStatusCode();

                throw new InvalidOperationException(
                    "EnsureSuccessStatusCode did not throw on a non-success response.");
            }
            catch (HttpRequestException thrown)
            {
                inner = thrown;
            }

            throw new ProviderTransportException(
                $"EDGAR answered {(int)response.StatusCode} for {request.Category} " +
                $"'{recordId}'. The status is carried as a closed-set token; nothing the service " +
                "wrote is recorded.",
                inner,
                ProviderTransportException.Classify(inner));
        }

        var payload = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        // A filing document with a successful status and no bytes is not a document, and for THIS
        // category only it is a failure rather than an empty answer.
        //
        // The distinction is about what the category means, not about being careful. For a price or
        // splits feed an empty body legitimately says "nothing in this window" and the archive
        // should hold exactly that. A filing document is one named document inside one accession the
        // issuer has already filed: it either has content or the request was wrong, and zero bytes
        // is never its content. Archiving it would leave a payload shaped exactly like a document
        // that was read and found to state no symbol, which is the inference F2 and F3 forbid - a
        // delivery failure becoming evidence of absence.
        //
        // Raised before the archive is touched, so nothing is stored and the run is recorded as
        // failed. The gateway's general success rule is untouched: no other category and no other
        // connector changes behaviour because of this.
        if (request.Category == DataCategory.RegulatoryFilingDocuments && payload.Length == 0)
        {
            throw new EmptyFilingDocumentException(
                $"EDGAR returned {(int)response.StatusCode} with an empty body for filing document " +
                $"'{recordId}'. A filing document has content or it has not been retrieved; a " +
                "zero-byte payload is not archived, because an empty document is indistinguishable " +
                "from one that was read and found to say nothing.");
        }

        return ProviderResponse.Create(
            payload,
            response.Content.Headers.ContentType?.MediaType ?? "application/json",
            _clock.UtcNow,
            sourceRecordId: recordId);

        // No continuation token: these endpoints return one complete document. Inventing paging
        // the provider does not offer would be building a request shape its terms never described.
    }

    /// <summary>
    /// What EDGAR can actually answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>supportsWindow: false</c> is a statement of fact, not a limitation to work around. The
    /// submissions and company-facts endpoints return a company's whole history in one document;
    /// there is no period parameter, and pretending otherwise would let a request for one quarter
    /// silently return everything.
    /// </para>
    /// <para>
    /// The quota comes from configuration but is bounded by the SEC's published ceiling in
    /// <see cref="SecEdgarOptions"/>, so it can be lowered by an operator and not raised past what
    /// the policy permits.
    /// </para>
    /// </remarks>
    private static ProviderCapabilities BuildCapabilities(int requestsPerSecond) =>
        ProviderCapabilities.Create(
            [
                DataCategory.RegulatoryFilings,
                DataCategory.CompanyProfile,
                DataCategory.FinancialStatements,
                DataCategory.EarningsDisclosure,
                DataCategory.MarketWideDisclosure,

                // Declared as of F4b so the connector can be exercised end to end against a fake
                // transport. Declaring a capability is NOT permission to use it: the registered
                // sec-edgar source does not list this category, so SourceAdmission refuses every
                // filing-document request before the gateway ever reaches a connector. Two gates,
                // and this opens only the one that says "this connector knows how".
                DataCategory.RegulatoryFilingDocuments,
            ],
            [Region.UnitedStates],

            // Two kinds, and the pairing is enforced in FetchAsync rather than here. Capabilities
            // are declared as sets and checked as sets, so this list alone would also permit a
            // company subject with a market-wide category. The connector refuses that pairing on
            // the way out, which is where the knowledge of which endpoint takes which subject
            // actually lives.
            ["Company", SecEdgarEndpoints.PeriodSubjectKind, FilingDocumentSubject.SubjectKind],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(requestsPerSecond));
}
