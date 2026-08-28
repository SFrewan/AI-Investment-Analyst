using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>What actually happened, measured after the horizon elapsed.</summary>
/// <remarks>
/// <para>
/// An outcome is measured with a <em>later</em> view of the world than the prediction had, and that
/// is not look-ahead: judging a forecast requires knowing what happened next. The leak the guard
/// exists to stop runs the other way - future information reaching the <em>prediction</em> - which is
/// why <see cref="PredictionRecord"/> refuses evidence younger than itself and this type does not.
/// </para>
/// <para>
/// What this type does insist on is that the outcome was observed after the horizon and not before
/// it. An outcome measured early is a different question answered by accident.
/// </para>
/// </remarks>
public sealed record RealisedOutcome
{
    public const string PrematureRule = "Validation.OutcomeMeasuredBeforeHorizon";

    private RealisedOutcome(
        IngestionSubject subject,
        DateTime measuredForUtc,
        DateTime observedAtUtc,
        Percentage realisedReturn)
    {
        Subject = subject;
        MeasuredForUtc = measuredForUtc;
        ObservedAtUtc = observedAtUtc;
        RealisedReturn = realisedReturn;
    }

    public IngestionSubject Subject { get; }

    /// <summary>The instant the outcome is the outcome of - the prediction's horizon.</summary>
    public DateTime MeasuredForUtc { get; }

    /// <summary>When the observation behind it became public.</summary>
    public DateTime ObservedAtUtc { get; }

    /// <summary>The realised move over the horizon.</summary>
    public Percentage RealisedReturn { get; }

    public static RealisedOutcome Create(
        IngestionSubject subject,
        DateTime measuredForUtc,
        DateTime observedAtUtc,
        Percentage realisedReturn)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(realisedReturn);

        DateRange.EnsureUtc(measuredForUtc, nameof(measuredForUtc));
        DateRange.EnsureUtc(observedAtUtc, nameof(observedAtUtc));

        if (observedAtUtc < measuredForUtc)
        {
            throw new DomainRuleViolationException(
                PrematureRule,
                $"the outcome for {measuredForUtc:O} was observed at {observedAtUtc:O}, before the " +
                "horizon it is supposed to measure had elapsed.");
        }

        return new RealisedOutcome(subject, measuredForUtc, observedAtUtc, realisedReturn);
    }

    public override string ToString() => $"{Subject} returned {RealisedReturn} by {MeasuredForUtc:O}";
}

/// <summary>How one prediction turned out.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero. The three non-judgements - unknown, unresolved and unavailable -
/// are kept apart from each other because they mean different things to a reader: one is a defect,
/// one is a prediction that has not had time yet, and one is a gap in the data. Collapsing them into
/// a single "excluded" bucket is how a report ends up quietly measuring a self-selected subset.
/// </remarks>
public enum OutcomeLabel
{
    Unknown = 0,

    /// <summary>Called positive, and it happened.</summary>
    TruePositive = 1,

    /// <summary>Called positive, and it did not. A false positive.</summary>
    FalsePositive = 2,

    /// <summary>Called negative, and it did not happen. Correctly stood aside.</summary>
    TrueNegative = 3,

    /// <summary>Called negative, and it happened anyway. A false negative - the missed opportunity.</summary>
    FalseNegative = 4,

    /// <summary>The horizon has not elapsed. Not yet judgeable.</summary>
    Unresolved = 5,

    /// <summary>No outcome data exists for this subject and horizon.</summary>
    Unavailable = 6,

    /// <summary>The system declined to call it, so there is nothing to score.</summary>
    Abstained = 7,
}

/// <summary>
/// Turns a prediction and what happened into a label. Pure, total, and explicit about not knowing.
/// </summary>
/// <remarks>
/// <para>
/// The threshold is an argument rather than a constant because "it happened" is a choice, and a
/// choice made in the measuring code is a choice nobody reads. Passing it in forces the report to
/// state it.
/// </para>
/// <para>
/// The threshold is compared with <c>&gt;=</c>, so a realised move exactly at the threshold counts as
/// the event occurring. Stated here because a boundary convention left implicit is one that changes
/// silently when somebody refactors the comparison.
/// </para>
/// </remarks>
public static class OutcomeLabeller
{
    public static OutcomeLabel Label(
        PredictionRecord prediction,
        RealisedOutcome? outcome,
        Percentage eventThreshold,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(eventThreshold);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (prediction.Direction == PredictionDirection.Abstain)
        {
            return OutcomeLabel.Abstained;
        }

        if (prediction.Direction == PredictionDirection.Unknown)
        {
            return OutcomeLabel.Unknown;
        }

        if (nowUtc < prediction.ResolvesAtUtc)
        {
            return OutcomeLabel.Unresolved;
        }

        if (outcome is null)
        {
            return OutcomeLabel.Unavailable;
        }

        if (outcome.MeasuredForUtc < prediction.ResolvesAtUtc)
        {
            // Judging a prediction on a shorter horizon than it was making is a different question,
            // and answering it here would make the rates depend on what data happened to be handy.
            return OutcomeLabel.Unavailable;
        }

        var happened = outcome.RealisedReturn.Ratio >= eventThreshold.Ratio;

        return (prediction.Direction, happened) switch
        {
            (PredictionDirection.Positive, true) => OutcomeLabel.TruePositive,
            (PredictionDirection.Positive, false) => OutcomeLabel.FalsePositive,
            (PredictionDirection.Negative, false) => OutcomeLabel.TrueNegative,
            (PredictionDirection.Negative, true) => OutcomeLabel.FalseNegative,
            _ => OutcomeLabel.Unknown,
        };
    }
}
