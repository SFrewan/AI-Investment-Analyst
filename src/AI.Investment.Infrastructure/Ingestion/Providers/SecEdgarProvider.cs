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

        var cik = SecEdgarEndpoints.NormaliseCik(request.Subject.Identifier);

        if (cik is null)
        {
            throw new InvalidOperationException(
                $"EDGAR identifies companies by CIK, and '{request.Subject}' does not contain one. " +
                "A ticker must be resolved to a CIK before ingestion.");
        }

        var path = SecEdgarEndpoints.ForCategory(request.Category, cik);

        if (path is null)
        {
            throw new InvalidOperationException(
                $"EDGAR serves no endpoint for {request.Category}. The capability check should have " +
                "refused this request before it reached the connector.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, path);

        // Required by the SEC's fair-access policy. Set per request rather than once on the
        // client so it cannot be silently lost by a client reconfigured elsewhere.
        message.Headers.UserAgent.ParseAdd(_options.UserAgent);
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
            sourceRecordId: $"CIK{cik}");

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
            ],
            [Region.UnitedStates],
            ["Company"],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(requestsPerSecond));
}
