using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Re-normalises an archived payload under the identity of the run that fetched it.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, and the shape of its constructor is part of the contract. It holds a lookup, an
/// archive and the normalisation pipeline. It holds <strong>no</strong> provider, no provider
/// catalogue, no rate limiter, no ingestion gateway and no acquisition authorisation - so "this
/// makes no network call and spends no acquisition unit" is a property of the type rather than a
/// promise in a comment. A future edit that added one of those would have to be a visible change to
/// this constructor, which is exactly where such a change should have to argue for itself.
/// </para>
/// <para>
/// <strong>The original run is not a preference; it is the correctness condition.</strong>
/// <see cref="NormalizationPipeline"/> keys its write on <c>normalization.record:{run.Id}</c>, and
/// that key is the only thing preventing a second replay from writing a second copy of every
/// observation: the observation store appends without a natural key, and each observation takes a
/// fresh identity on construction. This service therefore never accepts a run from its caller and
/// never constructs one. It is handed a payload, it asks which run archived that payload, and it
/// replays that run. The invariant reads:
/// </para>
/// <para>
/// <em>same original run + same archived bytes + same normaliser → the first replay may persist,
/// the second is suppressed by the seam, and the second cannot create duplicate observations.</em>
/// </para>
/// <para>
/// <strong>Historical quarantine records are left exactly as they are.</strong> A quarantine row is
/// evidence of what the normaliser decided when it first read those bytes, and that decision really
/// was made. A successful replay does not falsify it and does not erase it; the two coexist, and
/// the store is honest about both. Nothing here removes, rewrites or reinterprets a quarantine
/// record, and no destructive reconciliation is attempted.
/// </para>
/// <para>
/// Every refusal below returns a described result rather than throwing. A replay that cannot
/// proceed is an operational answer - the archive moved, the payload is shared, the run failed -
/// and an operator needs to read it, not catch it.
/// </para>
/// </remarks>
public sealed class ArchivedPayloadReplayService : IArchivedPayloadReplay
{
    private readonly IArchivedRunLookup _runs;
    private readonly IRawResponseArchive _archive;
    private readonly INormalizationPipeline _normalization;

    public ArchivedPayloadReplayService(
        IArchivedRunLookup runs,
        IRawResponseArchive archive,
        INormalizationPipeline normalization)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _normalization = normalization ?? throw new ArgumentNullException(nameof(normalization));
    }

    public async Task<ArchivedReplayResult> ReplayAsync(
        ContentHash payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var candidates = await _runs
            .RunsForArchivedPayloadAsync(payload, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return Refused(
                ArchivedReplayStatus.NoRunHoldsThisPayload,
                payload,
                $"No ingestion run records {payload.Abbreviated}. Replay only re-reads what a run "
                + "already fetched; there is no run under whose identity this could be written.");
        }

        if (candidates.Count > 1)
        {
            return Refused(
                ArchivedReplayStatus.MoreThanOneRunHoldsThisPayload,
                payload,
                $"{candidates.Count} runs record {payload.Abbreviated}, because the archive is "
                + "content-addressed and identical bytes are stored once. Replaying would have to "
                + "choose a subject for the observations, and choosing silently is how a series "
                + "ends up attributed to the wrong company.");
        }

        var run = candidates[0];

        if (run.Outcome is not (IngestionOutcome.Succeeded or IngestionOutcome.PartiallySucceeded))
        {
            return Refused(
                ArchivedReplayStatus.RunDidNotSucceed,
                payload,
                $"Run {run.Id} ended {run.Outcome}. A run that did not complete a retrieval does "
                + "not describe evidence, and replaying it would present a refusal or a failure as "
                + "data.",
                run.Id);
        }

        // Checked before the pipeline is asked for anything, and for every artifact rather than
        // only the one named. The pipeline's own answer to a missing payload is a quarantine record
        // under normalization.payload-missing@1 - accurate, but it would file a wrong archive root
        // as a data-quality defect, and those are not the same problem. The archive root is
        // resolved from the process working directory, so running from the wrong directory is the
        // likeliest way for this to be reached.
        foreach (var artifact in run.Artifacts)
        {
            var described = await _archive.DescribeAsync(artifact, cancellationToken).ConfigureAwait(false);

            if (described is not null)
            {
                continue;
            }

            return Refused(
                ArchivedReplayStatus.ArchiveNoLongerHoldsThePayload,
                payload,
                $"The archive does not hold {artifact.Abbreviated}, which run {run.Id} recorded. "
                + "Either retention removed it, or this process resolved a different archive root "
                + "than the one that wrote it. Nothing was replayed.",
                run.Id);
        }

        var summary = await _normalization.NormalizeAsync(run, cancellationToken).ConfigureAwait(false);

        return Describe(payload, run, summary);
    }

    /// <summary>
    /// Reads the pipeline's summary as one of three outcomes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline reports what it read and what it recorded; it does not report that the seam
    /// suppressed a duplicate, because the seam's answer is consumed inside it. The distinction is
    /// recoverable from the two counts, and the reasoning is worth stating rather than leaving to
    /// be re-derived.
    /// </para>
    /// <para>
    /// <c>PayloadsRead</c> counts payloads that were <em>not</em> quarantined, and a payload that
    /// yields no observation is quarantined - an empty series is refused under
    /// <c>market-data.empty-series@1</c> rather than recorded as no prices. So a run that read
    /// something built something. If it built something and recorded nothing, the write was
    /// suppressed, which is precisely the second-replay case.
    /// </para>
    /// <para>
    /// This inference is exact for the market-data normalisers, which are what replay exists for.
    /// A normaliser that returned a plain read with an empty observation list would break it - and
    /// would be describing "I read the document and it contained nothing", which is the situation
    /// the empty-series rule exists to refuse.
    /// </para>
    /// </remarks>
    private static ArchivedReplayResult Describe(
        ContentHash payload,
        IngestionRun run,
        NormalizationSummary summary)
    {
        if (summary.PayloadsRead == 0)
        {
            return new ArchivedReplayResult(
                ArchivedReplayStatus.PayloadStillUnreadable,
                payload,
                run.Id,
                summary,
                $"Run {run.Id} was replayed and the current normaliser still cannot read it: "
                + $"{summary.PayloadsQuarantined} payload(s) quarantined, nothing recorded. The "
                + "quarantine record already held for it is unchanged.");
        }

        if (summary.ObservationsRecorded == 0)
        {
            return new ArchivedReplayResult(
                ArchivedReplayStatus.AlreadyRecorded,
                payload,
                run.Id,
                summary,
                $"Run {run.Id} was read again and the seam suppressed the write as a duplicate: "
                + "its observations are already recorded. Nothing was added, and calling again "
                + "will do the same.");
        }

        var rejected = summary.RowsRejected == 0
            ? string.Empty
            : $" {summary.RowsRejected} row(s) were refused and are absent by design.";

        return new ArchivedReplayResult(
            ArchivedReplayStatus.Replayed,
            payload,
            run.Id,
            summary,
            $"Run {run.Id} was replayed from the archive: {summary.ObservationsRecorded} "
            + $"observation(s) recorded from {summary.PayloadsRead} payload(s)." + rejected
            + " No provider was contacted and no acquisition unit was spent.");
    }

    private static ArchivedReplayResult Refused(
        ArchivedReplayStatus status,
        ContentHash payload,
        string reason,
        IngestionRunId? runId = null) =>
        new(status, payload, runId, null, reason);
}
