using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// Fetches share splits from EODHD.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A second source rather than a second category on the first one.</strong> A registry
/// entry carries one cadence, one set of licensing terms and one freshness expectation, and prices
/// and splits do not share any of the three: a price series is expected every trading day and a
/// gap in it is a fault, while an instrument can go a decade without a split and silence means
/// nothing happened. Folding them together would make the freshness monitor report a healthy
/// splits feed as a fortnight stale, which is the fastest way to teach an operator to ignore it.
/// </para>
/// <para>
/// <strong>Why this feed exists at all.</strong> The market-data normaliser stores the raw close
/// and never <c>adjusted_close</c>, because the adjusted figure is retroactively rewritten by
/// every later corporate action and a point-in-time store cannot hold a number that changes
/// meaning. The cost of that correctness is a step in the series wherever a split happened, and
/// <see cref="Domain.Opportunities.Equity.SplitAdjustment"/> refuses any series carrying a step it
/// cannot explain. This connector is what supplies the explanation.
/// </para>
/// <para>
/// The credential handling, symbol validation and failure mapping are the price connector's, used
/// directly rather than copied. A second implementation of the redaction in particular is a second
/// place for it to drift, and what it protects is the one thing in this file that must never be
/// wrong.
/// </para>
/// </remarks>
public sealed class EodhdSplitsProvider : IDataProvider
{
    /// <summary>The registry key. Matches <see cref="EodhdSplitsSource"/>.</summary>
    public static readonly SourceId Id = SourceId.Create("eodhd-splits");

    private readonly HttpClient _httpClient;
    private readonly EodhdOptions _options;
    private readonly IClock _clock;

    public EodhdSplitsProvider(HttpClient httpClient, IOptions<EodhdOptions> options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        Capabilities = BuildCapabilities(_options.MaxRequestsPerMinute);
    }

    public SourceId SourceId => Id;

    public ProviderCapabilities Capabilities { get; }

    public async Task<ProviderResponse> FetchAsync(
        IngestionRequest request,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "No EODHD API key is configured. Set 'Providers:Eodhd:ApiKey' in the user-secrets " +
                "store or the environment variable Providers__Eodhd__ApiKey. The splits connector " +
                "shares the price connector's credential because it is the same subscription.");
        }

        var symbol = EodhdProvider.SafeSymbol(request.Subject);

        if (symbol is null)
        {
            throw new InvalidOperationException(
                $"EODHD identifies instruments as TICKER.EXCHANGE, and '{request.Subject}' is not " +
                "one. A sweep has no symbol, and an identifier carrying a separator or a query " +
                "character is not a ticker - it is an attempt to reach a different endpoint.");
        }

        var uri = BuildUri(symbol, request.Window);

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EodhdProvider.MediaType));

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(
                $"The EODHD splits request for '{symbol}' failed before a response was received: " +
                EodhdProvider.Redact(exception.Message, _options.ApiKey));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, symbol);
            }

            var payload = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            // Unlike the price endpoint, an EMPTY ARRAY here is the ordinary answer: most
            // instruments have never split. A zero-BYTE body is still a broken answer, and the two
            // are different facts - "[]" is four bytes saying nothing happened.
            if (payload.Length == 0)
            {
                throw new HttpRequestException(
                    $"EODHD answered the splits request for '{symbol}' with a success status and " +
                    "no body. An empty body and an empty list of splits are different facts, and " +
                    "recording this as the second would assert that the instrument has never " +
                    "split on the strength of a broken response.");
            }

            return ProviderResponse.Create(
                payload,
                response.Content.Headers.ContentType?.MediaType ?? EodhdProvider.MediaType,
                _clock.UtcNow,
                sourceRecordId: symbol);
        }
    }

    /// <summary>Builds the request URI, including the credential EODHD requires in the query.</summary>
    private string BuildUri(string symbol, DateRange? window)
    {
        var query = new List<string>(4)
        {
            "api_token=" + Uri.EscapeDataString(_options.ApiKey),
            "fmt=json",
        };

        if (window is not null)
        {
            query.Add("from=" + window.StartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            query.Add("to=" + window.EndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return $"api/splits/{Uri.EscapeDataString(symbol)}?{string.Join('&', query)}";
    }

    /// <summary>
    /// The failure for a status code, phrased so an operator knows whose problem it is.
    /// </summary>
    /// <remarks>
    /// A 404 means something different here than on the price endpoint, and saying so matters:
    /// there, it is a symbol that does not exist; here it can also be a plan that does not include
    /// corporate actions. Sending an operator to check the ticker when the subscription is the
    /// problem wastes the one thing this text exists to save.
    /// </remarks>
    private static HttpRequestException Failure(HttpStatusCode status, string symbol) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HttpRequestException(
                $"EODHD refused the splits request for '{symbol}' as unauthenticated or not " +
                "permitted (401/403). The key is wrong, or this subscription does not include " +
                "corporate actions.",
                inner: null,
                statusCode: status),

            HttpStatusCode.TooManyRequests => new HttpRequestException(
                $"EODHD rate-limited the splits request for '{symbol}' (429). The connector does " +
                "not retry: the gateway's limiter owns the pacing, and retrying underneath it " +
                "would spend quota nothing was counting.",
                inner: null,
                statusCode: status),

            HttpStatusCode.NotFound => new HttpRequestException(
                $"EODHD has no corporate-action record for '{symbol}' (404). The ticker or the " +
                "exchange suffix is wrong, or this plan does not include the splits endpoint. " +
                "Note that an instrument which has simply never split answers 200 with an empty " +
                "array, so a 404 is not that.",
                inner: null,
                statusCode: status),

            _ => new HttpRequestException(
                $"EODHD answered the splits request for '{symbol}' with {(int)status}.",
                inner: null,
                statusCode: status),
        };

    /// <summary>
    /// Corporate actions only, one category, windowed.
    /// </summary>
    /// <remarks>
    /// Declaring only what there is an endpoint for. A connector that claimed a category it could
    /// not serve would have requests routed to it and refused at the last moment, after the
    /// admission and rate-limit gates had already been spent on it.
    /// </remarks>
    private static ProviderCapabilities BuildCapabilities(int requestsPerMinute) =>
        ProviderCapabilities.Create(
            [DataCategory.CorporateActions],
            [Region.Global],
            [EodhdProvider.SecurityKind],
            supportsWindow: true,
            maxWindowDuration: null,
            quota: ProviderQuota.PerMinute(requestsPerMinute));
}
