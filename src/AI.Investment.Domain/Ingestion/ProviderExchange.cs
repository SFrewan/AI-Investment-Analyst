using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// What was asked of a provider, and what came back, for ONE request/response exchange.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The archive is content-addressed, and
/// <c>FileSystemRawResponseArchive.StoreAsync</c> returns early when the bytes are already stored.
/// That is correct for bytes and fatal for evidence: the two-byte payload <c>[]</c> is held by 324
/// runs, and its sidecar describes only the first writer. A store keyed by content cannot say what
/// 324 different requests asked, so the evidence has to hang off the run instead.
/// </para>
/// <para>
/// <strong>Why per exchange rather than per run.</strong> A run can page, so one run can make
/// several exchanges, each with its own status, its own bytes and its own retrieval time. The grain
/// of the evidence is the exchange; <see cref="ExchangeOrdinal"/> is what separates them.
/// </para>
/// <para>
/// <strong>Immutable after insertion.</strong> Every property is set once by <see cref="Record"/>
/// and never again. There is no mutator, no state transition and no correction path - which is also
/// why nothing that must not be stored may ever be <em>captured</em>: there is no later opportunity
/// to take it out.
/// </para>
/// <para>
/// <strong>It holds no secret.</strong> <see cref="EndpointTemplate"/> is a route template, never a
/// resolved URI; <see cref="RedactedRequestParameters"/> and <see cref="SelectedResponseHeaders"/>
/// are allow-listed at capture, in the connector, before they ever reach this type. The guards below
/// are a second line, not the first.
/// </para>
/// <para>
/// <strong>What it cannot say.</strong> Nothing about <em>why</em> a provider answered as it did. A
/// 200 with an empty array and no explanatory header proves the question and the answer, and stops
/// there.
/// </para>
/// </remarks>
public sealed class ProviderExchange
{
    public const int MaxEndpointTemplateLength = 300;
    public const int MaxFingerprintLength = 64;
    public const int MaxJsonLength = 4000;
    public const int MaxCorrelationIdLength = 200;

    /// <summary>
    /// Substrings that must never appear in a stored key or value, whatever the connector believed
    /// it was passing.
    /// </summary>
    /// <remarks>
    /// A deny-list is the wrong tool for deciding what to keep - the allow-lists in the application
    /// layer do that. This is the different job of refusing to persist something that is obviously a
    /// credential even if an allow-list is later widened by mistake. Belt, not braces.
    /// </remarks>
    private static readonly string[] ForbiddenSubstrings =
    [
        "api_token", "apikey", "api-key", "access_token", "authorization", "bearer",
        "password", "secret", "cookie",
    ];

    private ProviderExchange()
    {
        // EF materialisation only.
        SourceId = null!;
        RequestFingerprint = null!;
        EndpointTemplate = null!;
        RedactedRequestParameters = null!;
        SelectedResponseHeaders = null!;
    }

    private ProviderExchange(
        Guid id,
        IngestionRunId ingestionRunId,
        int exchangeOrdinal,
        SourceId sourceId,
        string requestFingerprint,
        string endpointTemplate,
        string redactedRequestParameters,
        DateTime? requestedFromUtc,
        DateTime? requestedToUtc,
        int? httpStatusCode,
        string selectedResponseHeaders,
        DateTime retrievedAtUtc,
        string? responseContentHash,
        int? responseByteLength,
        string? providerCorrelationId,
        DateTime recordedAtUtc)
    {
        Id = id;
        IngestionRunId = ingestionRunId;
        ExchangeOrdinal = exchangeOrdinal;
        SourceId = sourceId;
        RequestFingerprint = requestFingerprint;
        EndpointTemplate = endpointTemplate;
        RedactedRequestParameters = redactedRequestParameters;
        RequestedFromUtc = requestedFromUtc;
        RequestedToUtc = requestedToUtc;
        HttpStatusCode = httpStatusCode;
        SelectedResponseHeaders = selectedResponseHeaders;
        RetrievedAtUtc = retrievedAtUtc;
        ResponseContentHash = responseContentHash;
        ResponseByteLength = responseByteLength;
        ProviderCorrelationId = providerCorrelationId;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The run this exchange belongs to. Several exchanges may share it.</summary>
    public IngestionRunId IngestionRunId { get; private set; }

    /// <summary>0-based position of this exchange within its run's paging loop.</summary>
    public int ExchangeOrdinal { get; private set; }

    public SourceId SourceId { get; private set; }

    /// <summary>
    /// The request's fingerprint, frozen at capture.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed on read. The fingerprint algorithm can change; a recomputed
    /// value would be the fingerprint this request <em>would</em> have now, not the one it was made
    /// under - and the whole point of an evidence record is that it says what was true then.
    /// </remarks>
    public string RequestFingerprint { get; private set; }

    /// <summary>The route template, e.g. <c>api/eod/{symbol}</c>. Never a resolved URI.</summary>
    public string EndpointTemplate { get; private set; }

    /// <summary>Allow-listed request parameters, as canonical JSON. Never the credential.</summary>
    public string RedactedRequestParameters { get; private set; }

    /// <summary>The start of the range AS SENT - not as intended.</summary>
    /// <remarks>
    /// Captured separately from <c>IngestionRequest.Window</c> on purpose. Whether the two agree is
    /// precisely the question an empty response raises, and a field derived from the intent could
    /// never answer it.
    /// </remarks>
    public DateTime? RequestedFromUtc { get; private set; }

    /// <summary>The end of the range AS SENT. See <see cref="RequestedFromUtc"/>.</summary>
    public DateTime? RequestedToUtc { get; private set; }

    /// <summary>The response status as a plain integer. Null when no response was received.</summary>
    public int? HttpStatusCode { get; private set; }

    /// <summary>Allow-listed response headers, as canonical JSON.</summary>
    public string SelectedResponseHeaders { get; private set; }

    /// <summary>When THIS exchange's bytes were retrieved - not when the archive first saw them.</summary>
    public DateTime RetrievedAtUtc { get; private set; }

    /// <summary>
    /// The address of the bytes this exchange produced, or null when it produced none.
    /// </summary>
    /// <remarks>
    /// A plain string rather than <see cref="ContentHash"/>: this is a recorded observation about a
    /// past exchange, not a live reference the domain follows, and it must be able to be absent. Any
    /// value present here was produced by <c>ContentHash.Compute</c> over the archived bytes.
    /// </remarks>
    public string? ResponseContentHash { get; private set; }

    public int? ResponseByteLength { get; private set; }

    /// <summary>The vendor's own request identifier, when it sends one.</summary>
    public string? ProviderCorrelationId { get; private set; }

    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>Whether this exchange produced archived bytes.</summary>
    public bool ProducedAPayload => ResponseContentHash is not null;

    public static ProviderExchange Record(
        IngestionRunId ingestionRunId,
        int exchangeOrdinal,
        SourceId sourceId,
        string requestFingerprint,
        string endpointTemplate,
        string redactedRequestParameters,
        DateTime? requestedFromUtc,
        DateTime? requestedToUtc,
        int? httpStatusCode,
        string selectedResponseHeaders,
        DateTime retrievedAtUtc,
        string? responseContentHash,
        int? responseByteLength,
        string? providerCorrelationId,
        DateTime recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        if (exchangeOrdinal < 0)
        {
            throw new DomainValidationException(nameof(exchangeOrdinal),
                "An exchange ordinal is a position within a run's paging loop and cannot be negative.");
        }

        var fingerprint = Required(requestFingerprint, MaxFingerprintLength, nameof(requestFingerprint));
        var template = Required(endpointTemplate, MaxEndpointTemplateLength, nameof(endpointTemplate));
        var parameters = Required(redactedRequestParameters, MaxJsonLength, nameof(redactedRequestParameters));
        var headers = Required(selectedResponseHeaders, MaxJsonLength, nameof(selectedResponseHeaders));

        RefuseSecrets(template, nameof(endpointTemplate));
        RefuseSecrets(parameters, nameof(redactedRequestParameters));
        RefuseSecrets(headers, nameof(selectedResponseHeaders));

        if (retrievedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(nameof(retrievedAtUtc), "A retrieval timestamp must be UTC.");
        }

        if (recordedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(nameof(recordedAtUtc), "A recording timestamp must be UTC.");
        }

        if (responseContentHash is not null && responseContentHash.Length != ContentHash.HexLength)
        {
            throw new DomainValidationException(nameof(responseContentHash),
                "A response content hash must be the address the archive computed, or absent.");
        }

        if (responseByteLength is < 0)
        {
            throw new DomainValidationException(nameof(responseByteLength),
                "A response cannot have a negative length.");
        }

        return new ProviderExchange(
            Guid.NewGuid(),
            ingestionRunId,
            exchangeOrdinal,
            sourceId,
            fingerprint,
            template,
            parameters,
            Utc(requestedFromUtc, nameof(requestedFromUtc)),
            Utc(requestedToUtc, nameof(requestedToUtc)),
            httpStatusCode,
            headers,
            retrievedAtUtc,
            responseContentHash,
            responseByteLength,
            Optional(providerCorrelationId, MaxCorrelationIdLength),
            recordedAtUtc);
    }

    private static DateTime? Utc(DateTime? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Value.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(parameterName, "A request boundary must be UTC.");
        }

        return value;
    }

    private static string Required(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, "This field is required on an exchange record.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(parameterName,
                $"This field may not exceed {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static void RefuseSecrets(string value, string parameterName)
    {
        foreach (var forbidden in ForbiddenSubstrings)
        {
            if (value.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately does not echo the value. A message that quoted the offending text
                // would put the credential into an exception, a log and a ticket.
                throw new DomainValidationException(parameterName,
                    $"This field contains '{forbidden}', which is never permitted in an exchange "
                    + "record. Redaction belongs at capture, in the connector, because this record "
                    + "is append-only and cannot be corrected afterwards.");
            }
        }
    }
}
