using System.Globalization;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>Why a strategy may not compete for an action.</summary>
public enum AdmissionRefusal
{
    None = 0,

    /// <summary>No measurement exists at all.</summary>
    NeverMeasured = 1,

    /// <summary>The event was written down after the outcomes it scores were visible.</summary>
    EventDeclaredAfterTheEvidence = 2,

    /// <summary>The parameters were fitted to the dataset the score comes from.</summary>
    TunedOnTheEvidenceItIsScoredOn = 3,

    /// <summary>The declared event and the labelled event are not the same event.</summary>
    EventMismatch = 4,

    /// <summary>Too few predictions have resolved for the score to mean anything.</summary>
    NotEnoughResolvedPredictions = 5,

    /// <summary>The score could not be computed.</summary>
    CalibrationNotEstablished = 6,

    /// <summary>The score was computed and is worse than the bar.</summary>
    PoorlyCalibrated = 7,

    /// <summary>More variants have been tried within one search family than the budget allows.</summary>
    TooManyTrialsAgainstOneEvidenceBase = 8,

    /// <summary>The measurement is old enough that it describes a different market.</summary>
    EvidenceStale = 9,

    /// <summary>The measurement is of a different strategy than the one being admitted.</summary>
    MeasurementIsOfAnotherStrategy = 10,

    /// <summary>
    /// Enough predictions resolved, but too few of them were independent of each other.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NotEnoughResolvedPredictions"/> because it is a different failure
    /// with a different fix. Too few resolved means wait. Too few independent means the evidence
    /// base is narrower than its row count suggests, and waiting on the same base will not help.
    /// </remarks>
    NotEnoughIndependentEvidence = 11,

    /// <summary>
    /// The strategy cleared the shipped ceiling but did not do what it said it would do.
    /// </summary>
    /// <remarks>
    /// The other half of letting a strategy declare its own claim. A claim that buys a smaller
    /// derived sample has to be paid for, or claiming strongly would be free: declare a very strong
    /// score, get a small sample requirement, then deliver a mediocre one and pass on the shipped
    /// ceiling instead. Fires only when the claim is stricter than the ceiling, so a strategy that
    /// claims exactly the ceiling is judged by the ceiling and nothing here changes for it.
    /// </remarks>
    FellShortOfItsOwnClaim = 13,

    /// <summary>The score is no better than a forecaster who only knew the base rate.</summary>
    /// <remarks>
    /// A fixed ceiling cannot catch this. For an event that happens one time in ten, always saying
    /// "one in ten" scores 0.09, so a strategy scoring 0.20 passes a 0.20 ceiling while carrying
    /// strictly less information than saying nothing at all.
    /// </remarks>
    NoBetterThanTheBaseRate = 12,
}

/// <summary>
/// Whether one detection strategy's opportunities may enter the ranked pool.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is specific. When several strategies produce opportunities and something
/// ranks them together, the ranking silently rewards whichever strategy has the most optimistic
/// scorer. A rule nobody has measured will happily state 0.9 while a measured one states 0.4, and
/// the pool will act on the fiction - not because anyone decided to, but because 0.9 is larger than
/// 0.4 and nothing in the comparison knows the difference between a number that was checked and a
/// number that was asserted.
/// </para>
/// <para>
/// So admission is a separate decision from ranking, taken per strategy rather than per opportunity,
/// and it is about the strategy's record rather than about any one candidate. A strategy that fails
/// here still runs, still drafts, still records predictions and still accumulates the evidence that
/// would admit it later. It simply does not compete. That is what gives a new strategy a way to earn
/// its way in and no way to skip the queue.
/// </para>
/// </remarks>
public sealed record StrategyAdmission
{
    private StrategyAdmission(
        OpportunityType type,
        string? eventFingerprint,
        DateTime assessedAtUtc,
        IReadOnlyList<AdmissionRefusal> refusals,
        IReadOnlyList<string> reasons)
    {
        Type = type;
        EventFingerprint = eventFingerprint;
        AssessedAtUtc = assessedAtUtc;
        Refusals = refusals;
        Reasons = reasons;
    }

    public OpportunityType Type { get; }

    /// <summary>The declaration this verdict was taken against, when one existed.</summary>
    public string? EventFingerprint { get; }

    public DateTime AssessedAtUtc { get; }

    public IReadOnlyList<AdmissionRefusal> Refusals { get; }

    public IReadOnlyList<string> Reasons { get; }

    /// <summary>True only when nothing refused it.</summary>
    public bool IsAdmitted => Refusals.Count == 0;

    /// <summary>
    /// Judges one strategy against the bar. A null measurement is the ordinary state of a strategy
    /// that has just been written, and is refused as such rather than treated as an error.
    /// </summary>
    public static StrategyAdmission Evaluate(
        StrategyEvent declared,
        StrategyMeasurement? measurement,
        AdmissionCriteria criteria,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(criteria);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var refusals = new List<AdmissionRefusal>();
        var reasons = new List<string>();

        void Refuse(AdmissionRefusal refusal, string reason)
        {
            refusals.Add(refusal);
            reasons.Add(reason);
        }

        if (measurement is null)
        {
            Refuse(
                AdmissionRefusal.NeverMeasured,
                $"'{declared.Type}' has no validation measurement. A strategy that has not been " +
                "measured is not a strategy that was measured and found adequate, and ranking one " +
                "against the other would record them as the same thing.");

            return new StrategyAdmission(
                declared.Type, declared.Fingerprint, nowUtc, refusals, reasons);
        }

        if (measurement.Type != declared.Type)
        {
            Refuse(
                AdmissionRefusal.MeasurementIsOfAnotherStrategy,
                $"the declaration is for '{declared.Type}' and the measurement is of " +
                $"'{measurement.Type}'. Admitting one on the other's record is the exact confusion " +
                "this gate exists to prevent.");

            return new StrategyAdmission(
                declared.Type, declared.Fingerprint, nowUtc, refusals, reasons);
        }

        // Look-ahead, one level up from the per-prediction check. Each prediction can be perfectly
        // well formed while the event that scores them was chosen after the outcomes were visible.
        if (declared.DeclaredAtUtc > measurement.EarliestPredictionUtc)
        {
            Refuse(
                AdmissionRefusal.EventDeclaredAfterTheEvidence,
                Invariant($"the event was declared at {declared.DeclaredAtUtc:O}, after the ") +
                Invariant($"earliest prediction it scores at {measurement.EarliestPredictionUtc:O}. ") +
                (declared.Basis == DeclarationBasis.Backtest
                    ? "The declaration says so itself: this is a backtest, and a backtest cannot " +
                      "show that the event was not chosen once the outcomes were visible. The " +
                      "declaration is now on the record, so predictions made after it are the ones " +
                      "that can establish this strategy."
                    : "An event chosen once the outcomes are visible describes the past."));
        }

        // In-sample scoring. No arithmetic can see this, which is why it is declared.
        if (declared.WasTunedOnTheEvidence ||
            string.Equals(
                declared.TuningBasisFingerprint,
                measurement.EvidenceBaseFingerprint,
                StringComparison.Ordinal))
        {
            Refuse(
                AdmissionRefusal.TunedOnTheEvidenceItIsScoredOn,
                $"the parameters were chosen on '{declared.TuningBasisFingerprint}' and the score " +
                "comes from the same dataset. An in-sample score is a description of the data it was " +
                "fitted to, and it will not survive contact with the next year.");
        }

        // The Block 3B lesson, enforced for every strategy rather than by wiring: the probability
        // the strategy states and the event the labeller scored must be one event.
        if (!declared.Matches(measurement.LabelledThreshold))
        {
            Refuse(
                AdmissionRefusal.EventMismatch,
                Invariant($"the strategy declares a threshold of {declared.Threshold.Ratio:0.####} ") +
                Invariant($"and the labeller scored {measurement.LabelledThreshold.Ratio:0.####}. ") +
                "Scoring one against the other measures the mismatch, not the strategy.");
        }

        // The sample bar is derived per strategy from what it claims, then floored at the shipped
        // minimum, so it can rise above a hundred and can never fall below it.
        var requirement = SampleRequirement.For(declared, measurement, criteria);

        if (requirement.IsUnattainable)
        {
            Refuse(
                AdmissionRefusal.NoBetterThanTheBaseRate,
                requirement.Basis);
        }
        else if (measurement.ResolvedPredictions < requirement.Required)
        {
            Refuse(
                AdmissionRefusal.NotEnoughResolvedPredictions,
                Invariant($"{measurement.ResolvedPredictions} predictions have resolved against a ") +
                Invariant($"required {requirement.Required}. Predictions that were made but have ") +
                "not resolved are not evidence about anything yet. That requirement stands " +
                "because " + requirement.Basis);
        }
        else if (measurement.EffectiveResolvedPredictions < requirement.Required)
        {
            Refuse(
                AdmissionRefusal.NotEnoughIndependentEvidence,
                Invariant($"{measurement.ResolvedPredictions} predictions resolved, but they fall ") +
                Invariant($"into {measurement.IndependentClusters} clusters correlated at ") +
                Invariant($"{measurement.IntraClusterCorrelation:0.000}, which is worth ") +
                Invariant($"{measurement.EffectiveResolvedPredictions} independent observations ") +
                Invariant($"against a required {requirement.Required}. ") +
                "Counting rows rather than information is how a narrow evidence base passes for a " +
                "wide one.");
        }

        if (measurement.TrialsInFamily > criteria.MaximumTrialsPerSearchFamily)
        {
            Refuse(
                AdmissionRefusal.TooManyTrialsAgainstOneEvidenceBase,
                Invariant($"this is variant {measurement.TrialsInFamily} in the search family ") +
                Invariant($"'{declared.SearchFamily}', past a budget of ") +
                Invariant($"{criteria.MaximumTrialsPerSearchFamily}. ") +
                "Keeping the best of many variants tried on one dataset is a search, and the winner " +
                "of a search looks good by chance before it looks good by skill.");
        }

        if (nowUtc - measurement.MeasuredAtUtc > criteria.MaximumEvidenceAge)
        {
            Refuse(
                AdmissionRefusal.EvidenceStale,
                Invariant($"the measurement is dated {measurement.MeasuredAtUtc:O}, more than ") +
                Invariant($"{criteria.MaximumEvidenceAge} ago. Evidence about a market ages."));
        }

        if (!measurement.BrierScore.IsMeasured)
        {
            Refuse(
                AdmissionRefusal.CalibrationNotEstablished,
                $"the Brier score could not be computed: {measurement.BrierScore.Explanation}. An " +
                "unmeasurable score is not a passing score.");
        }
        else if (measurement.BrierScore.Value!.Value > criteria.MaximumBrierScore)
        {
            Refuse(
                AdmissionRefusal.PoorlyCalibrated,
                Invariant($"the Brier score is {measurement.BrierScore.Value!.Value:0.0000} ") +
                Invariant($"against a ceiling of {criteria.MaximumBrierScore:0.00}. ") +
                "For reference, always saying fifty per cent scores 0.2500.");
        }
        else if (requirement.ClaimedBrier < criteria.MaximumBrierScore &&
                 measurement.BrierScore.Value!.Value > requirement.ClaimedBrier)
        {
            Refuse(
                AdmissionRefusal.FellShortOfItsOwnClaim,
                Invariant($"the strategy claimed {requirement.ClaimedBrier:0.0000} and scored ") +
                Invariant($"{measurement.BrierScore.Value!.Value:0.0000}. ") +
                "The claim is what its sample requirement was computed from, so a claim that is not " +
                "met is a sample sized for a strategy that did not turn up.");
        }
        else if (measurement.ReferenceBrier is { } reference &&
                 measurement.BrierScore.Value!.Value >= reference)
        {
            Refuse(
                AdmissionRefusal.NoBetterThanTheBaseRate,
                Invariant($"the Brier score is {measurement.BrierScore.Value!.Value:0.0000} and a ") +
                Invariant($"forecaster who only knew the base rate of ") +
                Invariant($"{measurement.BaseRate!.Value:P2} would score {reference:0.0000}. ") +
                "Clearing a fixed ceiling while carrying no more information than the base rate is " +
                "the failure a fixed ceiling cannot see.");
        }

        return new StrategyAdmission(
            declared.Type, declared.Fingerprint, nowUtc, refusals, reasons);
    }

    public override string ToString() =>
        IsAdmitted
            ? $"'{Type}' is admitted to the ranked pool"
            : $"'{Type}' is NOT admitted ({Refusals.Count} reasons)";

    private static string Invariant(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
