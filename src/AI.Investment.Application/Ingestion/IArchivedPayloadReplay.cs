using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Re-reads a payload the archive already holds, through the current normaliser.
/// </summary>
/// <remarks>
/// <para>
/// The half of ingesting that does not need a provider. The archive exists so that a normaliser can
/// be fixed and the original bytes read again; until now nothing exposed that, so the only way to
/// re-interpret an archived payload was to fetch it a second time - which costs a rate limit, may
/// return a different document, and would consume an acquisition authorisation for evidence the
/// platform already had.
/// </para>
/// <para>
/// <strong>Replay is addressed by the payload, never by a run the caller supplies.</strong> That is
/// the whole safety design, and it is structural rather than advisory. The observation store is
/// append-only, <see cref="Domain.Observations.Observation"/> takes a fresh identity on every
/// construction, and the only thing standing between a second replay and a duplicated series is the
/// seam's idempotency key - which the normalisation pipeline derives from the ingestion run's
/// identity. A caller able to hand in a run of its own making could therefore duplicate an entire
/// price history by doing nothing more unusual than calling twice. Taking a
/// <see cref="ContentHash"/> and looking the run up removes that possibility from the type system
/// rather than from the documentation.
/// </para>
/// <para>
/// Nothing here bypasses the Action/Policy seam. The write still travels through
/// <see cref="NormalizationPipeline"/>, still proposes under
/// <see cref="Domain.Enums.Capability.DataIngestion"/> with no financial effect, and is still
/// audited whether it executes, is denied, or is suppressed as a duplicate.
/// </para>
/// </remarks>
public interface IArchivedPayloadReplay
{
    /// <summary>
    /// Re-normalises the run that archived <paramref name="payload"/>, recording what it reads.
    /// </summary>
    Task<ArchivedReplayResult> ReplayAsync(
        ContentHash payload,
        CancellationToken cancellationToken = default);
}

/// <summary>What a replay attempt did, or why it did nothing.</summary>
public enum ArchivedReplayStatus
{
    /// <summary>The payload was read and its observations were recorded.</summary>
    Replayed = 0,

    /// <summary>
    /// The payload was read, and the seam suppressed the write because this run's observations are
    /// already recorded. Calling again is safe and changes nothing.
    /// </summary>
    AlreadyRecorded = 1,

    /// <summary>
    /// The run was found and its bytes were read, but every payload it holds was quarantined, so
    /// there was nothing to record. Not a failure of replay - the document is still unreadable.
    /// </summary>
    PayloadStillUnreadable = 2,

    /// <summary>No ingestion run records this payload. Nothing may be replayed on its behalf.</summary>
    NoRunHoldsThisPayload = 3,

    /// <summary>
    /// More than one run records this payload, so replaying would have to guess which subject the
    /// observations belong to. Refused rather than resolved.
    /// </summary>
    MoreThanOneRunHoldsThisPayload = 4,

    /// <summary>The run did not succeed, so its artifacts do not describe a completed retrieval.</summary>
    RunDidNotSucceed = 5,

    /// <summary>
    /// The archive no longer holds every artifact the run recorded - retention removed it, or the
    /// process is running against a different archive root than the one that wrote it.
    /// </summary>
    ArchiveNoLongerHoldsThePayload = 6,
}

/// <summary>The outcome of one replay attempt.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Payload">The payload replay was asked for.</param>
/// <param name="RunId">The run that was replayed, when one was found.</param>
/// <param name="Normalization">The pipeline's own summary, when the pipeline ran.</param>
/// <param name="Reason">A sentence an operator can act on.</param>
public sealed record ArchivedReplayResult(
    ArchivedReplayStatus Status,
    ContentHash Payload,
    IngestionRunId? RunId,
    NormalizationSummary? Normalization,
    string Reason)
{
    /// <summary>Whether this call is the one that wrote observations.</summary>
    /// <remarks>
    /// False for <see cref="ArchivedReplayStatus.AlreadyRecorded"/>, which is the expected answer
    /// to a second replay and is not an error.
    /// </remarks>
    public bool RecordedObservations => Status == ArchivedReplayStatus.Replayed;

    /// <summary>Observations written by this call. Zero for every status but Replayed.</summary>
    public int ObservationsRecorded => Normalization?.ObservationsRecorded ?? 0;

    public override string ToString() => $"{Status}: {Reason}";
}
