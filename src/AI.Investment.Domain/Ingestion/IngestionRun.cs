using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// One attempt to draw data from one source, and what came of it.
/// </summary>
/// <remarks>
/// <para>
/// The data plane's equivalent of <see cref="Actions.ActionExecution"/>, and append-only for the
/// same reason. "Where did this number come from?" is answered by a claim's provenance; "was that
/// retrieval complete, and has this source been failing?" is answered here. A ledger that could be
/// tidied afterwards would answer neither.
/// </para>
/// <para>
/// <strong>Refusals are recorded, not discarded.</strong> A run refused because its source was
/// inactive or unlicensed produces a completed run with <see cref="IngestionOutcome.Refused"/> and
/// the admission rule that refused it. Without this, the most interesting thing that can happen -
/// the platform declining to ingest something it was configured to ingest - would leave no trace,
/// and the operator would see only an unexplained absence of data.
/// </para>
/// </remarks>
public sealed class IngestionRun : AggregateRoot<IngestionRunId>
{
    public const int MaxFailureReasonLength = 2000;

    private readonly List<ContentHash> _artifacts;

    private IngestionRun(IngestionRunId id, IngestionRequest request, DateTime startedAtUtc)
        : base(id)
    {
        Request = request;
        StartedAtUtc = startedAtUtc;
        Outcome = IngestionOutcome.InProgress;
        _artifacts = [];
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private IngestionRun()
    {
        Request = null!;
        _artifacts = [];
    }

    public IngestionRequest Request { get; private set; }

    /// <summary>Convenience accessor; the source is a property of the request.</summary>
    public SourceId SourceId => Request.SourceId;

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public IngestionOutcome Outcome { get; private set; }

    /// <summary>Content hashes of everything this run archived, in retrieval order.</summary>
    public IReadOnlyList<ContentHash> Artifacts => _artifacts;

    /// <summary>Why the run failed, or why it was refused. Null when it succeeded.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// The admission rule that refused this run, when it was refused. Null otherwise.
    /// </summary>
    public string? RefusalRuleId { get; private set; }

    public bool IsComplete => Outcome != IngestionOutcome.InProgress;

    /// <summary>Begins a run. The caller has already established that the source is admissible.</summary>
    public static IngestionRun Start(IngestionRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        return new IngestionRun(IngestionRunId.New(), request, nowUtc);
    }

    /// <summary>
    /// Records a run that never started, naming the rule that stopped it.
    /// </summary>
    /// <remarks>
    /// Takes a rule identifier and a reason rather than a specific result type, because a run can
    /// be stopped by more than one gate: source admission, provider capability, a rate limit, or
    /// the Action/Policy seam itself. All four are refusals from the operator's point of view, and
    /// all four belong in the same ledger with the rule that produced them.
    /// </remarks>
    public static IngestionRun Refuse(
        IngestionRequest request,
        string ruleId,
        string reason,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new DomainValidationException(
                nameof(ruleId),
                "A refusal must name the rule that produced it, otherwise the ledger records that " +
                "something was refused without recording what refused it.");
        }

        return new IngestionRun(IngestionRunId.New(), request, nowUtc)
        {
            Outcome = IngestionOutcome.Refused,
            CompletedAtUtc = nowUtc,
            RefusalRuleId = ruleId.Trim(),
            Reason = RequireReason(reason),
        };
    }

    /// <summary>
    /// Records a run refused by source admission.
    /// </summary>
    public static IngestionRun Refuse(
        IngestionRequest request,
        SourceAdmissionResult admission,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(admission);

        if (admission.IsAdmitted)
        {
            throw new DomainRuleViolationException(
                "IngestionRun.RefusalRequiresRefusal",
                "An admitted source cannot be recorded as a refusal. Recording a refusal that did " +
                "not happen would corrupt the only record of why data is missing.");
        }

        return Refuse(request, admission.RuleId!, admission.Reason!, nowUtc);
    }

    /// <summary>Records one archived payload.</summary>
    public void RecordArtifact(ContentHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        EnsureNotComplete();

        // The same payload retrieved twice within one run is one artifact. The archive is
        // content-addressed, so recording it twice would overstate what was retrieved.
        if (!_artifacts.Contains(hash))
        {
            _artifacts.Add(hash);
        }
    }

    public void MarkSucceeded(DateTime nowUtc) => Complete(IngestionOutcome.Succeeded, null, nowUtc);

    /// <summary>
    /// Records that some but not all of the request was satisfied.
    /// </summary>
    /// <param name="reason">
    /// What was missing. Must be safe to store permanently - this ledger is append-only and
    /// cannot be redacted, so nothing credential-shaped and no raw provider payload belongs here.
    /// </param>
    /// <param name="nowUtc">When the run ended.</param>
    public void MarkPartiallySucceeded(string reason, DateTime nowUtc) =>
        Complete(IngestionOutcome.PartiallySucceeded, RequireReason(reason), nowUtc);

    /// <inheritdoc cref="MarkPartiallySucceeded(string, DateTime)"/>
    public void MarkFailed(string reason, DateTime nowUtc) =>
        Complete(IngestionOutcome.Failed, RequireReason(reason), nowUtc);

    public override string ToString() =>
        $"{Request} [{Outcome}] artifacts={_artifacts.Count}";

    private void Complete(IngestionOutcome outcome, string? reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));
        EnsureNotComplete();

        if (nowUtc < StartedAtUtc)
        {
            throw new DomainRuleViolationException(
                "IngestionRun.CompletionFollowsStart",
                $"A run cannot complete ({nowUtc:O}) before it started ({StartedAtUtc:O}).");
        }

        Outcome = outcome;
        Reason = reason;
        CompletedAtUtc = nowUtc;
    }

    private void EnsureNotComplete()
    {
        if (IsComplete)
        {
            throw new DomainRuleViolationException(
                "IngestionRun.AlreadyComplete",
                $"Run {Id} already ended with {Outcome}. An ingestion record describes one attempt " +
                "and is not revised afterwards.");
        }
    }

    private static string RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                nameof(reason),
                "A run that did not fully succeed must say why, otherwise the ledger records that " +
                "something went wrong without recording what.");
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxFailureReasonLength
            ? trimmed
            : trimmed[..MaxFailureReasonLength];
    }
}
