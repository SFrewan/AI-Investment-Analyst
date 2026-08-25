using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// Hand-written doubles for the ingestion collaborators.
/// </summary>
/// <remarks>
/// Written by hand rather than generated, like the rest of this project's test infrastructure.
/// Each one records what it was asked to do, which is what the gateway's tests assert on: the
/// interesting claims are "the network was never touched" and "the refusal was written down",
/// and both are statements about calls that did or did not happen.
/// </remarks>
internal sealed class InMemorySourceRegistry : ISourceRegistry
{
    private readonly Dictionary<string, DataSource> _sources = new(StringComparer.Ordinal);

    public void Add(DataSource source) => _sources[source.Id.Value] = source;

    public Task<DataSource?> GetByIdAsync(SourceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sources.TryGetValue(id.Value, out var source) ? source : null);

    public Task<bool> ExistsAsync(SourceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sources.ContainsKey(id.Value));

    public Task<IReadOnlyList<DataSource>> FindSuppliersAsync(
        DataCategory category,
        Region region,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DataSource>>(
            _sources.Values.Where(s => s.Supplies(category, region)).ToList());

    public Task<IReadOnlyList<DataSource>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DataSource>>(_sources.Values.ToList());
}

internal sealed class StubProviderCatalogue : IProviderCatalogue
{
    private readonly List<IDataProvider> _providers = [];

    public void Add(IDataProvider provider) => _providers.Add(provider);

    public IDataProvider? Find(SourceId sourceId) =>
        _providers.FirstOrDefault(p => p.SourceId == sourceId);

    public IReadOnlyList<IDataProvider> All() => _providers;
}

/// <summary>A connector that returns canned pages and counts how often it was called.</summary>
internal sealed class FakeDataProvider : IDataProvider
{
    private readonly Queue<ProviderResponse> _pages;
    private readonly Exception? _throwOnFetch;

    public FakeDataProvider(
        SourceId sourceId,
        ProviderCapabilities capabilities,
        IEnumerable<ProviderResponse>? pages = null,
        Exception? throwOnFetch = null)
    {
        SourceId = sourceId;
        Capabilities = capabilities;
        _pages = new Queue<ProviderResponse>(pages ?? []);
        _throwOnFetch = throwOnFetch;
    }

    public SourceId SourceId { get; }

    public ProviderCapabilities Capabilities { get; }

    public int FetchCount { get; private set; }

    public Task<ProviderResponse> FetchAsync(
        IngestionRequest request,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        FetchCount++;

        if (_throwOnFetch is not null)
        {
            throw _throwOnFetch;
        }

        return Task.FromResult(_pages.Dequeue());
    }
}

internal sealed class RecordingArchive : IRawResponseArchive
{
    private readonly Dictionary<string, byte[]> _stored = new(StringComparer.Ordinal);

    public int StoreCount { get; private set; }

    public IReadOnlyCollection<string> Hashes => _stored.Keys;

    public Task<ContentHash> StoreAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> payload,
        string mediaType,
        DateTime retrievedAtUtc,
        CancellationToken cancellationToken = default)
    {
        StoreCount++;

        var hash = ContentHash.Compute(payload.Span);
        _stored[hash.Value] = payload.ToArray();

        return Task.FromResult(hash);
    }

    public Task<byte[]?> RetrieveAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stored.TryGetValue(hash.Value, out var bytes) ? bytes : null);

    public Task<bool> ExistsAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stored.ContainsKey(hash.Value));

    public Task<ArchivedPayload?> DescribeAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_described.TryGetValue(hash.Value, out var payload) ? payload : null);

    public Task DeleteAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        Deleted.Add(hash);
        _stored.Remove(hash.Value);
        _described.Remove(hash.Value);

        return Task.CompletedTask;
    }

    /// <summary>Seeds a payload with the metadata retention reads.</summary>
    public ContentHash Seed(byte[] payload, SourceId sourceId, DateTime retrievedAtUtc)
    {
        var hash = ContentHash.Compute(payload);
        _stored[hash.Value] = payload;
        _described[hash.Value] = new ArchivedPayload(sourceId, "application/json", retrievedAtUtc, payload.Length);

        return hash;
    }

    public List<ContentHash> Deleted { get; } = [];

    private readonly Dictionary<string, ArchivedPayload> _described = new(StringComparer.Ordinal);
}

internal sealed class StubPayloadReferenceIndex : IPayloadReferenceIndex
{
    private readonly bool _isReferenced;

    public StubPayloadReferenceIndex(bool isReferenced) => _isReferenced = isReferenced;

    public Task<bool> IsReferencedAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_isReferenced);
}

internal sealed class RecordingUnreplayableEvidenceStore : IUnreplayableEvidenceStore
{
    public List<UnreplayableEvidence> Recorded { get; } = [];

    public Task RecordAsync(UnreplayableEvidence marker, CancellationToken cancellationToken = default)
    {
        Recorded.Add(marker);
        return Task.CompletedTask;
    }

    public Task<UnreplayableEvidence?> FindAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.FirstOrDefault(m => m.Id == hash));

    public Task<bool> IsUnreplayableAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.Any(m => m.Id == hash));
}

internal sealed class RecordingRunStore : IIngestionRunStore
{
    public List<IngestionRun> Recorded { get; } = [];

    public Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default)
    {
        Recorded.Add(run);
        return Task.CompletedTask;
    }

    public Task<IngestionRun?> GetLatestForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.LastOrDefault(r => r.SourceId == sourceId));

    public Task<IngestionRun?> GetLatestSuccessfulForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.LastOrDefault(r =>
            r.SourceId == sourceId &&
            r.Outcome is IngestionOutcome.Succeeded or IngestionOutcome.PartiallySucceeded));

    public Task<bool> HasCompletedAsync(
        string requestFingerprint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.Any(r =>
            string.Equals(r.Request.Fingerprint(), requestFingerprint, StringComparison.Ordinal) &&
            r.Outcome == IngestionOutcome.Succeeded));

    public Task<IReadOnlyList<IngestionRun>> GetRecentAsync(
        DateTime sinceUtc,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IngestionRun>>(
            Recorded.Where(r => r.StartedAtUtc >= sinceUtc).Take(take).ToList());
}

internal sealed class StubRateLimiter : IProviderRateLimiter
{
    private readonly bool _allow;

    public StubRateLimiter(bool allow = true) => _allow = allow;

    public int AcquireAttempts { get; private set; }

    public Task<bool> TryAcquireAsync(
        SourceId sourceId,
        ProviderQuota quota,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        AcquireAttempts++;
        return Task.FromResult(_allow);
    }
}
