using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>
/// What a validation run actually measured about one strategy.
/// </summary>
/// <remarks>
/// Every field here comes from a run that has already happened; nothing is computed by the gate.
/// <see cref="BrierScore"/> is a <see cref="Measurement"/> rather than a <c>decimal?</c> so that
/// "not enough data to say" arrives as itself and is refused by name, instead of arriving as a
/// missing value that some caller defaults to something admissible.
/// </remarks>
public sealed record StrategyMeasurement
{
    private StrategyMeasurement(
        OpportunityType type,
        int resolvedPredictions,
        Measurement brierScore,
        Percentage labelledThreshold,
        DateTime earliestPredictionUtc,
        DateTime measuredAtUtc,
        string evidenceBaseFingerprint,
        int trialsInFamily,
        int familiesOnThisEvidenceBase,
        decimal? baseRate,
        decimal? componentStandardDeviation,
        int? independentClusters,
        decimal? intraClusterCorrelation)
    {
        Type = type;
        ResolvedPredictions = resolvedPredictions;
        BrierScore = brierScore;
        LabelledThreshold = labelledThreshold;
        EarliestPredictionUtc = earliestPredictionUtc;
        MeasuredAtUtc = measuredAtUtc;
        EvidenceBaseFingerprint = evidenceBaseFingerprint;
        TrialsInFamily = trialsInFamily;
        FamiliesOnThisEvidenceBase = familiesOnThisEvidenceBase;
        BaseRate = baseRate;
        ComponentStandardDeviation = componentStandardDeviation;
        IndependentClusters = independentClusters;
        IntraClusterCorrelation = intraClusterCorrelation;
    }

    public OpportunityType Type { get; }

    /// <summary>Predictions that resolved and were scored - not predictions that were made.</summary>
    public int ResolvedPredictions { get; }

    public Measurement BrierScore { get; }

    /// <summary>The threshold the labeller actually applied when it scored these predictions.</summary>
    public Percentage LabelledThreshold { get; }

    /// <summary>The earliest decision in the scored set, which the declaration must predate.</summary>
    public DateTime EarliestPredictionUtc { get; }

    public DateTime MeasuredAtUtc { get; }

    /// <summary>The dataset these predictions were scored against.</summary>
    public string EvidenceBaseFingerprint { get; }

    /// <summary>
    /// How many variants - including this one - have been measured within this strategy's search
    /// family. One means this is the first thing ever tried in it.
    /// </summary>
    public int TrialsInFamily { get; }

    /// <summary>
    /// How many distinct search families have been declared against this evidence base.
    /// </summary>
    /// <remarks>
    /// The other half of the multiple-comparisons control, and the half that stops families being
    /// a way out. Ten independent hypotheses on one dataset genuinely do raise the chance that one
    /// of them looks good, even when not one of them was searched for - so every derived sample
    /// size is computed at a significance divided by this count. It is a proportionate price rather
    /// than a cliff, and because the register counts families itself, a declaration cannot lower it.
    /// </remarks>
    public int FamiliesOnThisEvidenceBase { get; }

    /// <summary>
    /// How often the declared event actually happened on this evidence base.
    /// </summary>
    /// <remarks>
    /// Carried because it fixes what "no information" scores. A forecaster who knows only the base
    /// rate p and states it every time scores p(1-p), and that - not a fixed 0.25 - is the number a
    /// strategy has to beat. For a rare event the reference is low enough that a Brier of 0.20 is
    /// <em>worse</em> than saying nothing, which no fixed ceiling can see.
    /// </remarks>
    public decimal? BaseRate { get; }

    /// <summary>
    /// The measured spread of the individual Brier components.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed, because the sample size the gate derives is proportional to its
    /// square, and an assumed spread would be an assumed bar.
    /// </remarks>
    public decimal? ComponentStandardDeviation { get; }

    /// <summary>How many independent groups the resolved predictions fall into.</summary>
    /// <remarks>
    /// Null means the measurer did not cluster them, and the gate then counts rows exactly as it did
    /// before this field existed. Supplying it can only discount the count, never inflate it.
    /// </remarks>
    public int? IndependentClusters { get; }

    /// <summary>The measured correlation between outcomes inside one cluster.</summary>
    public decimal? IntraClusterCorrelation { get; }

    /// <summary>
    /// What a base-rate-only forecaster scores on this evidence: p(1-p). Null when unmeasured.
    /// </summary>
    public decimal? ReferenceBrier => BaseRate is { } p ? p * (1m - p) : null;

    /// <summary>
    /// The resolved count discounted for clustering - how many independent predictions it is worth.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="ResolvedPredictions"/> whenever the measurer supplied no clustering, so
    /// this is a tightening for measurements that carry the information and a no-op for those that
    /// do not.
    /// </remarks>
    public int EffectiveResolvedPredictions =>
        IndependentClusters is { } clusters && IntraClusterCorrelation is { } rho
            ? SampleRequirement.EffectiveSample(ResolvedPredictions, clusters, rho)
            : ResolvedPredictions;

    public static StrategyMeasurement Create(
        OpportunityType type,
        int resolvedPredictions,
        Measurement brierScore,
        Percentage labelledThreshold,
        DateTime earliestPredictionUtc,
        DateTime measuredAtUtc,
        string evidenceBaseFingerprint,
        int trialsInFamily = 1,
        int familiesOnThisEvidenceBase = 1,
        decimal? baseRate = null,
        decimal? componentStandardDeviation = null,
        int? independentClusters = null,
        decimal? intraClusterCorrelation = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(brierScore);
        ArgumentNullException.ThrowIfNull(labelledThreshold);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceBaseFingerprint);
        DateRange.EnsureUtc(earliestPredictionUtc, nameof(earliestPredictionUtc));
        DateRange.EnsureUtc(measuredAtUtc, nameof(measuredAtUtc));

        if (resolvedPredictions < 0)
        {
            throw new DomainValidationException(
                nameof(resolvedPredictions),
                "A negative count of resolved predictions is a defect in whatever counted them.");
        }

        if (familiesOnThisEvidenceBase < 1)
        {
            throw new DomainValidationException(
                nameof(familiesOnThisEvidenceBase),
                "A measurement that exists belongs to a family, so at least one family has been " +
                "declared against its evidence base.");
        }

        if (trialsInFamily < 1)
        {
            throw new DomainValidationException(
                nameof(trialsInFamily),
                "A measurement that exists is itself a trial, so the count is at least one. Zero " +
                "would let a search hide by never counting its first attempt.");
        }

        if (baseRate is { } rate && rate is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(baseRate),
                $"A base rate is a frequency in [0,1]; {rate} is not one.");
        }

        if (componentStandardDeviation is { } spread && spread < 0m)
        {
            throw new DomainValidationException(
                nameof(componentStandardDeviation),
                "A standard deviation is not negative. Whatever computed it is wrong, and a bar " +
                "derived from it would be wrong in an invisible direction.");
        }

        if (independentClusters is { } groups && groups < 1)
        {
            throw new DomainValidationException(
                nameof(independentClusters),
                "Predictions that exist fall into at least one cluster.");
        }

        if (intraClusterCorrelation is { } correlation && correlation is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(intraClusterCorrelation),
                $"An intra-cluster correlation lies in [0,1]; {correlation} is not one.");
        }

        if ((independentClusters is null) != (intraClusterCorrelation is null))
        {
            throw new DomainValidationException(
                nameof(independentClusters),
                "Clustering needs both the number of clusters and the correlation inside them. One " +
                "without the other cannot discount anything, and half a correction is worse than " +
                "none because it looks like one.");
        }

        if (measuredAtUtc < earliestPredictionUtc)
        {
            throw new DomainRuleViolationException(
                "Admission.MeasuredBeforeItWasPredicted",
                $"the run is dated {measuredAtUtc:O}, before the earliest prediction it scores at " +
                $"{earliestPredictionUtc:O}. One of the two timestamps is wrong, and either way the " +
                "measurement cannot be read.");
        }

        return new StrategyMeasurement(
            type,
            resolvedPredictions,
            brierScore,
            labelledThreshold,
            earliestPredictionUtc,
            measuredAtUtc,
            evidenceBaseFingerprint.Trim(),
            trialsInFamily,
            familiesOnThisEvidenceBase,
            baseRate,
            componentStandardDeviation,
            independentClusters,
            intraClusterCorrelation);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Type}: {ResolvedPredictions} resolved, Brier {BrierScore}");
}
