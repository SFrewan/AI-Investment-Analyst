using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.UnitTests.Normalization;

/// <summary>Hand-written doubles for the normalisation collaborators.</summary>
/// <remarks>
/// Each records what it was asked to do, because the pipeline's interesting claims are all
/// statements about calls that did or did not happen: the payload was never dropped, the denial
/// was written down, the second attempt did not duplicate the first.
/// </remarks>
internal sealed class RecordingObservationStore : IObservationStore
{
    public List<Observation> Recorded { get; } = [];

    public int RecordCalls { get; private set; }

    public Task RecordAsync(
        IReadOnlyList<Observation> observations,
        CancellationToken cancellationToken = default)
    {
        RecordCalls++;
        Recorded.AddRange(observations);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Observation>> ForSubjectAsync(
        IngestionSubject subject,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Observation>>(
            Recorded
                .Where(o => o.Subject == subject && o.PublishedAtUtc <= asAtUtc)
                .ToList());

    public Task<Observation?> LatestAsync(
        IngestionSubject subject,
        string attribute,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded
            .Where(o => o.Subject == subject
                        && string.Equals(o.Attribute, attribute, StringComparison.Ordinal)
                        && o.PublishedAtUtc <= asAtUtc)
            .OrderByDescending(o => o.PublishedAtUtc)
            .FirstOrDefault());
}

internal sealed class RecordingQuarantineStore : IQuarantineStore
{
    public List<QuarantinedPayload> Recorded { get; } = [];

    public Task RecordAsync(QuarantinedPayload payload, CancellationToken cancellationToken = default)
    {
        Recorded.Add(payload);

        return Task.CompletedTask;
    }

    public Task<bool> IsQuarantinedAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Recorded.Any(p => p.Id == hash));

    public Task<IReadOnlyList<QuarantinedPayload>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QuarantinedPayload>>(
            Recorded.OrderByDescending(p => p.QuarantinedAtUtc).Take(take).ToList());
}

/// <summary>A normaliser with a canned answer, which counts how often it was asked.</summary>
internal sealed class StubNormalizer : INormalizer
{
    private readonly Func<NormalizationInput, NormalizationResult> _answer;
    private readonly SourceId? _only;
    private readonly DataCategory? _onlyCategory;

    public StubNormalizer(
        Func<NormalizationInput, NormalizationResult> answer,
        SourceId? only = null,
        DataCategory? onlyCategory = null)
    {
        _answer = answer;
        _only = only;
        _onlyCategory = onlyCategory;
    }

    public int Calls { get; private set; }

    public List<ContentHash> Seen { get; } = [];

    public bool CanNormalize(SourceId sourceId, DataCategory category) =>
        (_only is null || sourceId == _only) &&
        (_onlyCategory is null || category == _onlyCategory);

    public Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        Seen.Add(input.ContentHash);

        return Task.FromResult(_answer(input));
    }
}
