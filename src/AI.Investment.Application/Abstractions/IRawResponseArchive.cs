using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Stores exactly what a source returned, addressed by the hash of its own bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes Phase 2's exit criterion reachable: <em>any analysis replays byte-identically
/// from stored raw responses</em>. An analysis records the content hashes it read; replaying it
/// means fetching those same bytes back and getting the same answer. Without the archive, a replay
/// re-fetches from the provider and compares this year's answer against last year's data - which
/// tests the provider, not the analysis.
/// </para>
/// <para>
/// <strong>Content-addressed, so writes are idempotent.</strong> Storing a payload that is already
/// present is a no-op that returns the existing hash. A daily poll of an unchanged document costs
/// one row, not three hundred and sixty-five.
/// </para>
/// <para>
/// <strong>Never parsed here.</strong> The archive stores bytes and a media type. Interpreting
/// them is normalisation's job, and an archive that understood its contents would have to be
/// migrated every time a provider changed its schema - defeating the point of keeping the
/// original.
/// </para>
/// <para>
/// <strong>What must not be archived.</strong> Request headers, query strings and anything else
/// that can carry an API key. The archive is long-lived and is read during investigations; a
/// credential written into it is a credential that outlives every rotation.
/// </para>
/// </remarks>
public interface IRawResponseArchive
{
    /// <summary>
    /// Stores the payload if it is not already present and returns its content hash.
    /// </summary>
    /// <param name="sourceId">The source the payload came from, for attribution and retention.</param>
    /// <param name="payload">The exact bytes received. Not normalised, not re-encoded.</param>
    /// <param name="mediaType">The media type the source declared, for example "application/json".</param>
    /// <param name="retrievedAtUtc">When these bytes were received.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ContentHash> StoreAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> payload,
        string mediaType,
        DateTime retrievedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the archived bytes, or null when nothing is stored under that hash.</summary>
    Task<byte[]?> RetrieveAsync(ContentHash hash, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(ContentHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// What is known about an archived payload without reading it, or null if it is not held.
    /// </summary>
    /// <remarks>
    /// Retention needs the retrieval time and the source, and needs them for payloads it is about
    /// to consider deleting. Reading megabytes of JSON to learn a timestamp would make a sweep
    /// cost as much as a re-ingest.
    /// </remarks>
    Task<ArchivedPayload?> DescribeAsync(ContentHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every payload the archive currently holds, in no guaranteed order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists for the retention sweep, which has to ask "what is here?" before it can ask "may this
    /// still be kept?". Nothing else needs it: ingestion addresses payloads by hash and
    /// normalisation reads the hashes a run recorded.
    /// </para>
    /// <para>
    /// Streamed rather than returned as a list. An archive is expected to outgrow memory long
    /// before it outgrows disk, and a sweep that had to materialise every hash first would fail at
    /// exactly the size where sweeping starts to matter.
    /// </para>
    /// <para>
    /// <strong>No ordering is promised</strong>, deliberately. A filesystem implementation walks
    /// directories, and pretending the result is sorted would invite a caller to depend on it. A
    /// sweep that needs a bound should take one; it must not assume the oldest come first.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<ContentHash> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes an archived payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The only caller is retention enforcement, and only after the Action/Policy seam has
    /// authorised the deletion under <c>Capability.DataRetention</c>.</strong> This is the single
    /// operation in the platform that destroys evidence, so it exists as a narrow, named method
    /// rather than as a general-purpose one, and nothing else has any business calling it.
    /// </para>
    /// <para>
    /// Deleting a payload that is not held is not an error. A retry after a partial failure should
    /// converge rather than throw.
    /// </para>
    /// </remarks>
    Task DeleteAsync(ContentHash hash, CancellationToken cancellationToken = default);
}

/// <summary>What the archive knows about a payload besides its bytes.</summary>
/// <param name="SourceId">The source it came from.</param>
/// <param name="MediaType">What the source said it was.</param>
/// <param name="RetrievedAtUtc">When it was fetched - the clock retention runs against.</param>
/// <param name="ByteLength">Its size.</param>
public sealed record ArchivedPayload(
    SourceId SourceId,
    string MediaType,
    DateTime RetrievedAtUtc,
    int ByteLength);
