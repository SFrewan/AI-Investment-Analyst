using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion;

/// <summary>
/// A content-addressed archive of provider responses on the local filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are named by their own SHA-256, which gives three properties for free. Writes are
/// idempotent, so a daily poll of an unchanged document costs one file rather than one per day.
/// Tampering is detectable, because altered bytes no longer answer to the name a claim recorded.
/// And replay is exact: an analysis that recorded a hash can be re-run against the same bytes it
/// originally read, which is what the phase's exit criterion asks for.
/// </para>
/// <para>
/// <strong>Writes are atomic.</strong> Content is written to a temporary file and then moved into
/// place. A process killed mid-write leaves a temporary file, not a truncated payload sitting under
/// a hash that no longer describes it - which would be worse than losing the payload, because it
/// would be silently wrong.
/// </para>
/// <para>
/// Files are fanned out two levels by hash prefix. A single flat directory holding hundreds of
/// thousands of entries is slow to enumerate on most filesystems and unpleasant on all of them.
/// </para>
/// <para>
/// A sidecar records the media type, the source and the retrieval time. It is metadata about the
/// fetch, kept beside the payload rather than inside it, so the archived bytes stay exactly what
/// the provider returned. <strong>No request detail is written</strong> - no URL, no headers, no
/// query string - because those can carry an API key and this store is long-lived and read during
/// investigations.
/// </para>
/// </remarks>
public sealed class FileSystemRawResponseArchive : IRawResponseArchive
{
    private const string PayloadExtension = ".bin";
    private const string MetadataExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _rootPath;

    public FileSystemRawResponseArchive(IOptions<RawArchiveOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    public async Task<ContentHash> StoreAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> payload,
        string mediaType,
        DateTime retrievedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var hash = ContentHash.Compute(payload.Span);
        var payloadPath = PathFor(hash, PayloadExtension);

        if (File.Exists(payloadPath))
        {
            // Already archived. The bytes are identical by definition - that is what the address
            // means - so re-writing them would be work with no effect.
            return hash;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);

        await WriteAtomicAsync(payloadPath, payload, cancellationToken).ConfigureAwait(false);

        var metadata = JsonSerializer.SerializeToUtf8Bytes(
            new ArchivedResponseMetadata(
                sourceId.Value,
                mediaType,
                retrievedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                payload.Length),
            JsonOptions);

        await WriteAtomicAsync(PathFor(hash, MetadataExtension), metadata, cancellationToken)
            .ConfigureAwait(false);

        return hash;
    }

    public async Task<byte[]?> RetrieveAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var path = PathFor(hash, PayloadExtension);

        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ExistsAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return Task.FromResult(File.Exists(PathFor(hash, PayloadExtension)));
    }

    public async Task<ArchivedPayload?> DescribeAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var metadataPath = PathFor(hash, MetadataExtension);

        if (!File.Exists(metadataPath) || !File.Exists(PathFor(hash, PayloadExtension)))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize<ArchivedResponseMetadata>(bytes, JsonOptions);

        if (metadata is null)
        {
            return null;
        }

        return new ArchivedPayload(
            SourceId.Create(metadata.SourceId),
            metadata.MediaType,
            DateTime.Parse(
                metadata.RetrievedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            metadata.ByteLength);
    }

    /// <summary>
    /// Walks the fan-out directories and yields every payload held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yields as it walks rather than collecting first, so a sweep's memory does not grow with the
    /// archive. A missing root is an empty archive, not an error: nothing has been fetched yet.
    /// </para>
    /// <para>
    /// A file whose name is not a content hash is skipped rather than thrown on. Temporary files
    /// from an interrupted write live in these directories by design, and a sweep that died on one
    /// would be stopped by exactly the debris it exists to tolerate.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ContentHash> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The filesystem walk below is synchronous, and without this the whole directory tree
        // would be traversed on the caller's thread before the first hash came back. Yielding once
        // makes the enumeration genuinely asynchronous, which is what the signature promises.
        await Task.Yield();

        if (!Directory.Exists(_rootPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(
                     _rootPath,
                     "*" + PayloadExtension,
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(path);

            if (!ContentHash.TryCreate(name, out var hash))
            {
                continue;
            }

            yield return hash;
        }
    }

    /// <summary>
    /// Permanently removes a payload and its sidecar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only by retention enforcement, and only after the Action/Policy seam has authorised
    /// the deletion. Nothing else in this system removes archived evidence.
    /// </para>
    /// <para>
    /// The payload goes first and the sidecar second. If the process dies between them, what
    /// remains is a sidecar describing bytes that are gone - which the next pass treats as "not
    /// archived" and cleans up - rather than bytes with nothing describing where they came from.
    /// </para>
    /// <para>
    /// Deleting what is not there is not an error, so a retry after a partial failure converges.
    /// </para>
    /// </remarks>
    public Task DeleteAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var payloadPath = PathFor(hash, PayloadExtension);

        if (File.Exists(payloadPath))
        {
            File.Delete(payloadPath);
        }

        var metadataPath = PathFor(hash, MetadataExtension);

        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        return Task.CompletedTask;
    }

    private string PathFor(ContentHash hash, string extension) =>
        Path.Combine(
            _rootPath,
            hash.Value[..2],
            hash.Value[2..4],
            hash.Value + extension);

    private static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("n");

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // overwrite: false - two callers racing on the same content would otherwise each
            // replace the other's identical file for no reason.
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another caller won the race and wrote the same bytes under the same address. There
            // is nothing to reconcile: content addressing means both wrote the same thing.
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record ArchivedResponseMetadata(
        string SourceId,
        string MediaType,
        string RetrievedAtUtc,
        int ByteLength);
}
