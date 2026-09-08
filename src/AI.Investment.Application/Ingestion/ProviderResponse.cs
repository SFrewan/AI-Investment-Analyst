namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Exactly what a connector received, before anything interprets it.
/// </summary>
/// <remarks>
/// <para>
/// Bytes, not a parsed object. Parsing happens in normalisation, after the payload has been
/// archived, so that the archived copy is the provider's answer rather than this build's reading
/// of it. A schema change then breaks normalisation - which is visible and fixable - instead of
/// silently changing what history says the provider replied.
/// </para>
/// <para>
/// <strong>Carries no request detail.</strong> No headers, no URL, no query string. Those can hold
/// an API key, and this object is handed to the archive, which is long-lived and read during
/// investigations. A credential written there outlives every rotation.
/// </para>
/// </remarks>
public sealed class ProviderResponse
{
    public const int MaxMediaTypeLength = 200;
    public const int MaxContinuationTokenLength = 2000;

    private ProviderResponse(
        ReadOnlyMemory<byte> payload,
        string mediaType,
        DateTime retrievedAtUtc,
        string? sourceRecordId,
        string? continuationToken,
        RequestProvenance? provenance)
    {
        Payload = payload;
        MediaType = mediaType;
        RetrievedAtUtc = retrievedAtUtc;
        SourceRecordId = sourceRecordId;
        ContinuationToken = continuationToken;
        Provenance = provenance;
    }

    /// <summary>The exact bytes received. Not normalised, not re-encoded.</summary>
    /// <remarks>
    /// <see cref="ReadOnlyMemory{T}"/> rather than <c>byte[]</c>: an array property hands every
    /// caller a mutable reference to the provider's answer, and the one thing an archived response
    /// must be is unaltered.
    /// </remarks>
    public ReadOnlyMemory<byte> Payload { get; }

    public string MediaType { get; }

    public DateTime RetrievedAtUtc { get; }

    /// <summary>
    /// The provider's own identifier for this record, when it has one - a filing accession
    /// number, an article id. Becomes <c>Provenance.SourceRecordId</c>.
    /// </summary>
    public string? SourceRecordId { get; }

    /// <summary>
    /// An opaque token for the next page, when the provider paginates. Null on the last page.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose: the caller loops until it is null and never constructs one. A caller
    /// that builds its own paging parameters is a caller that will eventually build one the
    /// provider's terms do not permit.
    /// </remarks>
    public string? ContinuationToken { get; }

    public bool HasMore => ContinuationToken is not null;

    /// <summary>
    /// The redacted description of the exchange that produced these bytes, when the connector
    /// captured one. Null from a connector that has not been instrumented yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONE place request detail is permitted on this object, and it is permitted only
    /// because <see cref="RequestProvenance"/> is allow-listed at construction and can hold no
    /// credential. The remark above still governs everything else: the caller must not pass this to
    /// the archive, and <c>IngestionGateway</c> does not - <c>IRawResponseArchive.StoreAsync</c>
    /// takes payload, media type and retrieval time as explicit arguments and never sees this
    /// object.
    /// </para>
    /// <para>
    /// Optional so that a connector which captures nothing behaves exactly as it did before, rather
    /// than being forced to invent evidence it does not have.
    /// </para>
    /// </remarks>
    public RequestProvenance? Provenance { get; }

    public static ProviderResponse Create(
        ReadOnlyMemory<byte> payload,
        string mediaType,
        DateTime retrievedAtUtc,
        string? sourceRecordId = null,
        string? continuationToken = null,
        RequestProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException(
                "A response must declare its media type, otherwise nothing downstream can know how " +
                "to read the archived bytes.",
                nameof(mediaType));
        }

        var trimmedMediaType = mediaType.Trim();

        if (trimmedMediaType.Length > MaxMediaTypeLength)
        {
            throw new ArgumentException(
                $"A media type may not exceed {MaxMediaTypeLength} characters.",
                nameof(mediaType));
        }

        if (retrievedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A retrieval timestamp must be UTC.",
                nameof(retrievedAtUtc));
        }

        var token = Normalise(continuationToken, MaxContinuationTokenLength, nameof(continuationToken));

        return new ProviderResponse(
            payload,
            trimmedMediaType,
            retrievedAtUtc,
            Normalise(sourceRecordId, 200, nameof(sourceRecordId)),
            token,
            provenance);
    }

    private static string? Normalise(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"'{parameterName}' may not exceed {maxLength} characters.",
                parameterName);
        }

        return trimmed;
    }
}
