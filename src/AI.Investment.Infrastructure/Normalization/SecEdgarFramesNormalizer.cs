using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Archives a cross-sectional frame and produces no observations from it, deliberately.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Emitting nothing is the behaviour, not a stub.</strong> A frames document is a list of
/// thousands of filers and one figure each for one period. Every observation in this platform is a
/// fact about a subject with a publication instant, and a frame carries neither: it has no
/// <c>filed</c> date, because it is EDGAR's own assembly of whatever each company most recently
/// reported for that period, restatements included. Turning its rows into observations would
/// stamp them with a publication time nobody published at, and every point-in-time judgement made
/// from them afterwards would be quietly wrong.
/// </para>
/// <para>
/// So the payload is archived - which is what a frame is fetched for, since the universe manifest
/// reads it directly - and the observation store is left alone. The run records what it fetched
/// and records that it produced nothing, which is the truth rather than an omission.
/// </para>
/// <para>
/// <strong>Why this type exists at all rather than no normaliser.</strong> With none registered,
/// the pipeline quarantines the payload under <c>normalization.no-normalizer</c>. That is a
/// reasonable default for a category nobody planned for, and the wrong record for one that was:
/// the quarantine table is where unreadable evidence goes, and a frame this platform deliberately
/// declines to interpret is not unreadable. It would also make the acquisition's
/// "quarantined payloads unchanged" check fire on every frame fetched.
/// </para>
/// </remarks>
public sealed class SecEdgarFramesNormalizer : INormalizer
{
    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return sourceId == SecEdgarProvider.Id && category == DataCategory.MarketWideDisclosure;
    }

    public Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // No parsing at all, not even a validity check. Reading the document here would be the
        // first step towards emitting from it, and the reason for emitting nothing is that the
        // rows cannot carry an honest publication instant - which no amount of parsing fixes.
        return Task.FromResult(NormalizationResult.Normalized([]));
    }
}
