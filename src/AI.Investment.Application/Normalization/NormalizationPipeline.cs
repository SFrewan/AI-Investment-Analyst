using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Normalization;

/// <summary>
/// Reads what an ingestion run archived and turns it into observations.
/// </summary>
/// <remarks>
/// <para>
/// The second half of ingesting. The first half establishes that a source may be used and captures
/// exactly what it said; this one decides what those bytes mean. Keeping them apart is what makes
/// the archive worth having: a normaliser can be fixed and re-run against the original bytes,
/// because reading them was never allowed to change them.
/// </para>
/// <para>
/// <strong>A payload that cannot be read is quarantined, never dropped.</strong> Failure to
/// normalise is evidence - of a changed schema, a wrong assumption, or a genuinely malformed
/// response - and all three deserve investigating. Discarded, they become indistinguishable from
/// data that simply never arrived.
/// </para>
/// <para>
/// <strong>Writing observations goes through the seam.</strong> An observation is something the
/// platform believes, which makes recording one domain state and a side effect like any other.
/// Quarantine records do not: like the ingestion ledger they must be writable precisely when
/// nothing is authorised, because a policy denial is one of the things worth quarantining a run
/// over.
/// </para>
/// <para>
/// One proposal per run rather than per payload. A run is the unit an operator reasons about, and
/// a proposal per artifact would bury the audit trail under rows that all say the same thing.
/// </para>
/// </remarks>
public sealed class NormalizationPipeline : INormalizationPipeline
{
    public const string ServiceId = "application.normalization.pipeline";
    public const string ServiceVersion = "1.0";

    /// <summary>No registered normaliser reads that source's payloads for that category.</summary>
    public const string NoNormalizerRule = "normalization.no-normalizer@1";

    /// <summary>The archive no longer holds the bytes the run recorded.</summary>
    public const string PayloadMissingRule = "normalization.payload-missing@1";

    private static readonly ActionType NormalizeActionType = ActionType.Create("normalization.record");
    private static readonly ProposedBy Proposer = ProposedBy.Service(ServiceId, ServiceVersion);

    private readonly IRawResponseArchive _archive;
    private readonly IEnumerable<INormalizer> _normalizers;
    private readonly IObservationStore _observations;
    private readonly IQuarantineStore _quarantine;
    private readonly IActionGateway _gateway;
    private readonly IClock _clock;

    public NormalizationPipeline(
        IRawResponseArchive archive,
        IEnumerable<INormalizer> normalizers,
        IObservationStore observations,
        IQuarantineStore quarantine,
        IActionGateway gateway,
        IClock clock)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _normalizers = normalizers ?? throw new ArgumentNullException(nameof(normalizers));
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<NormalizationSummary> NormalizeAsync(
        IngestionRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var request = run.Request;
        var normalizer = FindNormalizer(request.SourceId, request.Category);
        var observations = new List<Observation>();
        var quarantined = 0;
        var read = 0;

        foreach (var hash in run.Artifacts)
        {
            var outcome = await ReadAsync(normalizer, request, hash, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.IsQuarantined)
            {
                await QuarantineAsync(hash, request, outcome, cancellationToken).ConfigureAwait(false);
                quarantined++;

                continue;
            }

            read++;
            observations.AddRange(outcome.Observations);
        }

        var recorded = observations.Count == 0
            ? 0
            : await RecordAsync(run, observations, cancellationToken).ConfigureAwait(false);

        return new NormalizationSummary(read, recorded, quarantined);
    }

    private INormalizer? FindNormalizer(Domain.Sources.SourceId sourceId, DataCategory category)
    {
        foreach (var normalizer in _normalizers)
        {
            if (normalizer.CanNormalize(sourceId, category))
            {
                return normalizer;
            }
        }

        return null;
    }

    private async Task<NormalizationResult> ReadAsync(
        INormalizer? normalizer,
        IngestionRequest request,
        ContentHash hash,
        CancellationToken cancellationToken)
    {
        if (normalizer is null)
        {
            return NormalizationResult.Quarantine(
                NoNormalizerRule,
                $"No normaliser reads {request.Category} from '{request.SourceId}'. The payload is " +
                "archived and can be re-read once one exists.");
        }

        // Described first: the retrieval time is what every resulting observation's provenance is
        // built from, and asking for it separately avoids reading megabytes to learn a timestamp
        // when the payload turns out to be gone.
        var described = await _archive.DescribeAsync(hash, cancellationToken).ConfigureAwait(false);
        var payload = described is null
            ? null
            : await _archive.RetrieveAsync(hash, cancellationToken).ConfigureAwait(false);

        if (described is null || payload is null)
        {
            // Either retention deleted it under licence, or the archive lost it. Both are worth
            // knowing about, and neither is a reason to invent observations.
            return NormalizationResult.Quarantine(
                PayloadMissingRule,
                $"The archive no longer holds {hash.Abbreviated}, which the run recorded.");
        }

        return await normalizer.NormalizeAsync(
                new NormalizationInput(
                    request.SourceId,
                    request.Category,
                    request.Subject,
                    hash,
                    payload,
                    described.RetrievedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task QuarantineAsync(
        ContentHash hash,
        IngestionRequest request,
        NormalizationResult outcome,
        CancellationToken cancellationToken)
    {
        if (await _quarantine.IsQuarantinedAsync(hash, cancellationToken).ConfigureAwait(false))
        {
            // The same bytes fail the same way. One record per payload is more useful than one
            // per attempt, and re-recording would make a retry look like a new problem.
            return;
        }

        var record = QuarantinedPayload.Record(
            hash,
            request.SourceId,
            request.Category,
            outcome.RuleId!,
            outcome.Reason!,
            _clock.UtcNow);

        await _quarantine.RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RecordAsync(
        IngestionRun run,
        List<Observation> observations,
        CancellationToken cancellationToken)
    {
        var request = run.Request;

        var proposal = ActionProposal.Create(
            request.CorrelationId,
            Capability.DataIngestion,
            NormalizeActionType,
            ActionTarget.Create(request.Subject.Kind, request.Subject.Identifier),
            new NormalizationParameters(request.SourceId, request.Category, observations.Count),

            // Recording what a source said costs nothing and can be superseded by a later
            // observation; nothing is overwritten.
            ActionEconomics.NoFinancialEffect(),
            Proposer,

            // Keyed on the run: normalising the same run twice must not double its observations.
            $"normalization.record:{run.Id}",
            _clock.UtcNow);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async token =>
            {
                await _observations.RecordAsync(observations, token).ConfigureAwait(false);

                return observations.Count;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.WasExecuted ? outcome.Result : 0;
    }
}
