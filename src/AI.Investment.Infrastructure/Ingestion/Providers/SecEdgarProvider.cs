using System.Net.Http.Headers;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
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

        // Two request shapes, kept apart here rather than merged.
        //
        // Every other category names a company and resolves to a CIK path. A market-wide frame
        // names a PERIOD and has no CIK at all. Routing them through one identifier check would
        // mean either accepting a period where a company belongs or the reverse, and the failure
        // would not be visible: a company path built from a period subject returns somebody's
        // accounts, and it would be archived as a cross-section of the market.
        string path;
        string recordId;

        if (request.Category == DataCategory.MarketWideDisclosure)
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

        using var message = new HttpRequestMessage(HttpMethod.Get, path);

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

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        // Throws on any non-success status. An empty response and a failed request mean opposite
        // things to a ledger, and conflating them turns a failure into a silent gap.
        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

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
            ],
            [Region.UnitedStates],

            // Two kinds, and the pairing is enforced in FetchAsync rather than here. Capabilities
            // are declared as sets and checked as sets, so this list alone would also permit a
            // company subject with a market-wide category. The connector refuses that pairing on
            // the way out, which is where the knowledge of which endpoint takes which subject
            // actually lives.
            ["Company", SecEdgarEndpoints.PeriodSubjectKind],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(requestsPerSecond));
}
