using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>Which way a prediction pointed.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero so that an unparsed or defaulted prediction is not silently
/// counted as a call in either direction. It is excluded from the rates and reported as such.
/// </remarks>
public enum PredictionDirection
{
    Unknown = 0,

    /// <summary>The system said act: it expected the subject to move in the favourable direction.</summary>
    Positive = 1,

    /// <summary>The system said do not act.</summary>
    Negative = 2,

    /// <summary>The system declined to call it either way.</summary>
    Abstain = 3,
}

/// <summary>
/// One prediction under test, reduced to the few things it can be judged on.
/// </summary>
/// <remarks>
/// <para>
/// A prediction is judged on four things and nothing else: what it was about, when it was made, which
/// way it pointed, and how sure it claimed to be. Everything else - the reasoning, the evidence, the
/// model version - belongs in the audit trail, and is referenced here rather than copied, so that a
/// measurement cannot be made from a summary that has drifted from what actually happened.
/// </para>
/// <para>
/// <strong>The constructor is the look-ahead guard.</strong> A prediction is refused outright if the
/// latest evidence behind it became public after the moment it was supposedly made. That check has to
/// live here rather than in the engine that builds these, because look-ahead bias does not arrive as
/// a bug report - it arrives as unusually good results - and a rule that a caller can forget is a
/// rule that will eventually be forgotten by the caller who is in a hurry.
/// </para>
/// </remarks>
public sealed record PredictionRecord
{
    public const string LookaheadRule = "Validation.NoLookaheadEvidence";

    public const string UnresolvableRule = "Validation.PredictionMustBeAboutTheFuture";

    private PredictionRecord(
        Guid predictionId,
        IngestionSubject subject,
        DateTime decidedAtUtc,
        DateTime resolvesAtUtc,
        PredictionDirection direction,
        Percentage? statedProbability,
        Confidence? statedConfidence,
        CalculationVersion methodology,
        DateTime evidenceAvailableAtUtc,
        string sourceReference)
    {
        PredictionId = predictionId;
        Subject = subject;
        DecidedAtUtc = decidedAtUtc;
        ResolvesAtUtc = resolvesAtUtc;
        Direction = direction;
        StatedProbability = statedProbability;
        StatedConfidence = statedConfidence;
        Methodology = methodology;
        EvidenceAvailableAtUtc = evidenceAvailableAtUtc;
        SourceReference = sourceReference;
    }

    public Guid PredictionId { get; }

    public IngestionSubject Subject { get; }

    /// <summary>When the prediction was made. The knowledge cutoff it must be judged against.</summary>
    public DateTime DecidedAtUtc { get; }

    /// <summary>When the horizon elapses and the prediction becomes judgeable.</summary>
    public DateTime ResolvesAtUtc { get; }

    public PredictionDirection Direction { get; }

    /// <summary>The stated chance of success, when the prediction carried one. The calibration input.</summary>
    public Percentage? StatedProbability { get; }

    /// <summary>The stated epistemic confidence, when it carried one.</summary>
    public Confidence? StatedConfidence { get; }

    /// <summary>The version of the method that produced it, so a report can say what it measured.</summary>
    public CalculationVersion Methodology { get; }

    /// <summary>The latest publication time among the inputs behind it.</summary>
    public DateTime EvidenceAvailableAtUtc { get; }

    /// <summary>Where the underlying record lives, so every number stays traceable.</summary>
    public string SourceReference { get; }

    /// <summary>The cutoff this prediction was entitled to see.</summary>
    public KnowledgeCutoff Cutoff => KnowledgeCutoff.At(DecidedAtUtc);

    /// <summary>True when the system made a call rather than declining to.</summary>
    public bool IsCall => Direction is PredictionDirection.Positive or PredictionDirection.Negative;

    public static PredictionRecord Create(
        Guid predictionId,
        IngestionSubject subject,
        DateTime decidedAtUtc,
        DateTime resolvesAtUtc,
        PredictionDirection direction,
        CalculationVersion methodology,
        DateTime evidenceAvailableAtUtc,
        string sourceReference,
        Percentage? statedProbability = null,
        Confidence? statedConfidence = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);

        DateRange.EnsureUtc(decidedAtUtc, nameof(decidedAtUtc));
        DateRange.EnsureUtc(resolvesAtUtc, nameof(resolvesAtUtc));
        DateRange.EnsureUtc(evidenceAvailableAtUtc, nameof(evidenceAvailableAtUtc));

        if (predictionId == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(predictionId),
                "A prediction under test must be identifiable, or its result cannot be traced back " +
                "to the record it came from.");
        }

        if (resolvesAtUtc <= decidedAtUtc)
        {
            throw new DomainRuleViolationException(
                UnresolvableRule,
                $"a prediction made at {decidedAtUtc:O} that resolves at {resolvesAtUtc:O} is about " +
                "the past or the present. There is nothing to be right about.");
        }

        if (evidenceAvailableAtUtc > decidedAtUtc)
        {
            throw new DomainRuleViolationException(
                LookaheadRule,
                $"the latest evidence behind this prediction became public at " +
                $"{evidenceAvailableAtUtc:O}, after the prediction was supposedly made at " +
                $"{decidedAtUtc:O}. Measuring it would measure hindsight.");
        }

        return new PredictionRecord(
            predictionId,
            subject,
            decidedAtUtc,
            resolvesAtUtc,
            direction,
            statedProbability,
            statedConfidence,
            methodology,
            evidenceAvailableAtUtc,
            sourceReference.Trim());
    }

    public override string ToString() =>
        $"{Subject} {Direction} at {DecidedAtUtc:O} resolving {ResolvesAtUtc:O}";
}
