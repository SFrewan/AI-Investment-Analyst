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
/// Connector for EODHD's end-of-day price API. The first real market-data vendor.
/// </summary>
/// <remarks>
/// <para>
/// It fetches bytes and nothing else - no parsing, no reshaping, no field extraction. That is
/// normalisation's job and it happens after the archive has stored what EODHD actually returned.
/// If the vendor changes a JSON shape, normalisation breaks visibly instead of history quietly
/// changing its account of what a market did.
/// </para>
/// <para>
/// <strong>The key never leaves this class.</strong> EODHD authenticates through an
/// <c>api_token</c> query parameter, so the credential is unavoidably part of the request URI -
/// which is exactly the sort of string that ends up in an exception message, a log scope or a
/// telemetry span. So nothing here ever puts a URI into a message: the send is wrapped, every
/// failure is re-thrown with a message this class wrote, and <see cref="Redact"/> is applied to
/// anything that came from outside. A test asserts the key appears in nothing thrown.
/// </para>
/// <para>
/// <strong>It does not retry.</strong> The gateway owns the run, its ledger and its rate limiter;
/// a connector that retried underneath would spend quota the limiter had not accounted for and
/// turn one recorded failure into several unrecorded ones. Every failure here is one failure,
/// reported upwards with the vendor's status code preserved in the text.
/// </para>
/// </remarks>
public sealed class EodhdProvider : IDataProvider
{
    /// <summary>The registry key. Matches <see cref="EodhdSource"/>.</summary>
    public static readonly SourceId Id = SourceId.Create("eodhd-eod");

    /// <summary>The subject kind this connector understands.</summary>
    public const string SecurityKind = "Security";

    /// <summary>The media type EODHD answers with, and the one payloads are recorded under.</summary>
    public const string MediaType = "application/json";

    /// <summary>The longest ticker this connector will put in a request.</summary>
    public const int MaxIdentifierLength = 32;

    /// <summary>What a redacted credential looks like in anything this class throws.</summary>
    public const string Redaction = "***";

    private readonly HttpClient _httpClient;
    private readonly EodhdOptions _options;
    private readonly IClock _clock;

    public EodhdProvider(HttpClient httpClient, IOptions<EodhdOptions> options, IClock clock)
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
            // Fail closed, and say which knob is empty without printing anything from it.
            throw new InvalidOperationException(
                "No EODHD API key is configured. Set 'Providers:Eodhd:ApiKey' in the user-secrets " +
                "store or the environment variable Providers__Eodhd__ApiKey. The connector does " +
                "not fall back to an unauthenticated request, because EODHD answers one with a " +
                "page that is not price data.");
        }

        var symbol = SafeSymbol(request.Subject);

        if (symbol is null)
        {
            throw new InvalidOperationException(
                $"EODHD identifies instruments as TICKER.EXCHANGE, and '{request.Subject}' is not " +
                "one. A sweep has no symbol, and an identifier carrying a separator or a query " +
                "character is not a ticker - it is an attempt to reach a different endpoint.");
        }

        var uri = BuildUri(symbol, request.Window);

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // The transport's own message can name the request. Ours cannot.
            throw new HttpRequestException(
                $"The EODHD request for '{symbol}' failed before a response was received: " +
                Redact(exception.Message));
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

            if (payload.Length == 0)
            {
                // A zero-byte 200 is not an empty series; it is a broken answer. Archiving it
                // would put a payload in the ledger that normalisation could only quarantine,
                // under a rule about JSON rather than about the vendor.
                throw new HttpRequestException(
                    $"EODHD answered the request for '{symbol}' with a success status and no body. " +
                    "An empty body and an empty series are different facts, and recording this as " +
                    "the second would invent a market that did not trade.");
            }

            return ProviderResponse.Create(
                payload,
                response.Content.Headers.ContentType?.MediaType ?? MediaType,
                _clock.UtcNow,
                sourceRecordId: symbol);

            // No continuation token. The end-of-day endpoint returns the whole requested range in
            // one document; inventing paging the vendor does not offer would be building a request
            // shape its terms never described.
        }
    }

    /// <summary>
    /// The failure for a status code, phrased so an operator knows whose problem it is.
    /// </summary>
    /// <remarks>
    /// Kept distinct because the responses to them differ: a 401 is a credential to fix, a 429 is a
    /// schedule to slow down, a 404 is a symbol that does not exist on that exchange, and a 5xx is
    /// somebody else's outage. Collapsing them into "the request failed" would send an operator to
    /// check the key during a vendor outage.
    /// </remarks>
    private static HttpRequestException Failure(HttpStatusCode status, string symbol) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HttpRequestException(
                $"EODHD refused the request for '{symbol}' as unauthenticated or not permitted " +
                $"({(int)status}). The configured key is wrong, expired, or its plan does not " +
                "cover this data. The key itself is not shown here.",
                inner: null,
                statusCode: status),

            HttpStatusCode.TooManyRequests => new HttpRequestException(
                $"EODHD rate-limited the request for '{symbol}' (429). The connector does not " +
                "retry: the gateway's limiter owns the pacing, and retrying underneath it would " +
                "spend quota nothing was counting. Lower 'Providers:Eodhd:MaxRequestsPerMinute'.",
                inner: null,
                statusCode: status),

            HttpStatusCode.NotFound => new HttpRequestException(
                $"EODHD has no end-of-day series for '{symbol}' (404). The ticker or the exchange " +
                "suffix is wrong, or the plan does not include that market.",
                inner: null,
                statusCode: status),

            _ => new HttpRequestException(
                $"EODHD answered the request for '{symbol}' with {(int)status}.",
                inner: null,
                statusCode: status),
        };

    /// <summary>Builds the request URI, including the credential EODHD requires in the query.</summary>
    /// <remarks>
    /// Relative to the client's base address. <c>fmt=json</c> and <c>period=d</c> are stated rather
    /// than left to the vendor's defaults, so a change to those defaults cannot silently alter what
    /// the archive holds.
    /// </remarks>
    private string BuildUri(string symbol, DateRange? window)
    {
        var query = new List<string>(5)
        {
            "api_token=" + Uri.EscapeDataString(_options.ApiKey),
            "fmt=json",
            "period=d",
        };

        if (window is not null)
        {
            query.Add("from=" + window.StartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            query.Add("to=" + window.EndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return $"api/eod/{Uri.EscapeDataString(symbol)}?{string.Join('&', query)}";
    }

    /// <summary>Replaces the configured key wherever it appears in text from outside.</summary>
    internal string Redact(string text) => Redact(text, _options.ApiKey);

    /// <summary>
    /// The same redaction, for the other connector that carries this credential.
    /// </summary>
    /// <remarks>
    /// Static and shared rather than copied. The splits connector sends the same key on the same
    /// subscription, and a second implementation of this would be a second place for it to drift -
    /// which, for the one routine whose whole job is to keep a credential out of an exception
    /// message, is not a risk worth taking to save eight lines.
    /// </remarks>
    internal static string Redact(string text, string apiKey)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(apiKey))
        {
            return text;
        }

        var withoutRaw = text.Replace(apiKey, Redaction, StringComparison.Ordinal);

        // The URI carries the escaped form, which is a different string when the key contains a
        // character that needed escaping.
        return withoutRaw.Replace(
            Uri.EscapeDataString(apiKey),
            Redaction,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The subject's identifier when it is an EODHD symbol, and null when it is not.
    /// </summary>
    /// <remarks>
    /// Letters, digits, dot and hyphen, with exactly one dot separating ticker from exchange.
    /// Everything else is refused rather than escaped: escaping turns a malformed identifier into
    /// a valid request for something else, and refusing turns it into a recorded failure.
    /// </remarks>
    internal static string? SafeSymbol(IngestionSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!string.Equals(subject.Kind, SecurityKind, StringComparison.Ordinal))
        {
            return null;
        }

        var identifier = subject.Identifier;

        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > MaxIdentifierLength)
        {
            return null;
        }

        var dots = 0;

        foreach (var c in identifier)
        {
            if (c == '.')
            {
                dots++;

                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return null;
            }
        }

        if (dots != 1)
        {
            return null;
        }

        var separator = identifier.IndexOf('.', StringComparison.Ordinal);

        // Neither half may be empty: ".US" and "AAPL." are not symbols.
        return separator == 0 || separator == identifier.Length - 1 ? null : identifier;
    }

    /// <summary>The exchange suffix of a symbol this connector accepted.</summary>
    public static string ExchangeOf(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var separator = symbol.LastIndexOf('.');

        return separator < 0 ? string.Empty : symbol[(separator + 1)..];
    }

    /// <summary>The ticker half of a symbol this connector accepted.</summary>
    public static string TickerOf(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var separator = symbol.LastIndexOf('.');

        return separator < 0 ? symbol : symbol[..separator];
    }

    /// <summary>
    /// What EODHD's end-of-day endpoint can answer.
    /// </summary>
    /// <remarks>
    /// <c>supportsWindow: true</c>, unlike every other connector here, because this endpoint really
    /// does take <c>from</c> and <c>to</c>. Declaring it lets the gateway ask for the range it wants
    /// rather than pulling a whole history to use a month of it.
    /// <para>
    /// <see cref="DataCategory.MarketPrices"/> only. EODHD sells fundamentals, news, options and
    /// several asset classes; none of them is needed to activate the observation window, and a
    /// connector that declared categories it had no endpoint for would have its requests routed
    /// here and refused at the last moment.
    /// </para>
    /// <para>
    /// <see cref="Region.Global"/> because the exchange suffix, not the connector, decides the
    /// market.
    /// </para>
    /// </remarks>
    private static ProviderCapabilities BuildCapabilities(int requestsPerMinute) =>
        ProviderCapabilities.Create(
            [DataCategory.MarketPrices],
            [Region.Global],
            [SecurityKind],
            supportsWindow: true,
            maxWindowDuration: null,
            quota: ProviderQuota.PerMinute(requestsPerMinute));
}
