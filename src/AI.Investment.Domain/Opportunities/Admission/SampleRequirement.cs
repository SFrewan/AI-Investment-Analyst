using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>The inverse of the standard normal cumulative distribution.</summary>
/// <remarks>
/// Written out rather than pulled from a statistics package because the admission bar depends on
/// it, and a dependency that could change its answer between versions would silently move the bar.
/// This is Acklam's rational approximation, accurate to about 1.15e-9 across the open interval,
/// which is several orders of magnitude tighter than anything the sample sizes here are sensitive
/// to.
/// </remarks>
public static class StandardNormal
{
    private const double Low = 0.02425;

    private static readonly double[] A =
    {
        -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
        1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
    };

    private static readonly double[] B =
    {
        -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
        6.680131188771972e+01, -1.328068155288572e+01,
    };

    private static readonly double[] C =
    {
        -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
        -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
    };

    private static readonly double[] D =
    {
        7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
        3.754408661907416e+00,
    };

    /// <summary>The value z for which the standard normal puts <paramref name="probability"/> below z.</summary>
    public static double Quantile(double probability)
    {
        if (probability is <= 0d or >= 1d || double.IsNaN(probability))
        {
            throw new DomainValidationException(
                nameof(probability),
                "A quantile is defined strictly inside (0,1). Zero and one are infinities, and a " +
                "sample size derived from an infinity is not a sample size.");
        }

        double q;
        double r;

        if (probability < Low)
        {
            q = Math.Sqrt(-2d * Math.Log(probability));

            return (((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                   ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1d);
        }

        if (probability > 1d - Low)
        {
            q = Math.Sqrt(-2d * Math.Log(1d - probability));

            return -(((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                   ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1d);
        }

        q = probability - 0.5d;
        r = q * q;

        return (((((A[0] * r + A[1]) * r + A[2]) * r + A[3]) * r + A[4]) * r + A[5]) * q /
               (((((B[0] * r + B[1]) * r + B[2]) * r + B[3]) * r + B[4]) * r + 1d);
    }
}

/// <summary>
/// How many resolved predictions this particular strategy must produce before its score means
/// anything - derived from what it claims, never chosen, and never below the floor.
/// </summary>
/// <remarks>
/// <para>
/// A single constant cannot serve every strategy. At the platform's own ceiling of 0.20 against a
/// fifty-fifty reference of 0.25, a hundred resolved predictions puts the difference about 1.7
/// standard errors from the reference - which is not enough to conclude anything at conventional
/// power. The sample that would be is closer to two hundred. Meanwhile a strategy genuinely
/// calibrated at 0.10 establishes itself in a few dozen. One number is simultaneously too lenient
/// where it matters most and arbitrary where the evidence is strongest.
/// </para>
/// <para>
/// So the requirement is <em>derived</em> from the strategy's own declared claim and the base rate
/// of the event on the evidence base, and then floored. <see cref="Required"/> is
/// <c>max(floor, derived)</c>: it can rise above the shipped hundred and it can never fall below
/// it. A strategy that wants a lighter bar cannot get one - the only thing a stronger claim buys is
/// a smaller derived term that the floor then absorbs. That property is what makes this a
/// tightening rather than a negotiation.
/// </para>
/// <para>
/// The claim is declared on <see cref="StrategyEvent"/>, inside its fingerprint, so it cannot be
/// revised after the score is seen. The reference is computed from the measured base rate rather
/// than declared, so it cannot be chosen to flatter. Neither half is under the strategy's control
/// at the moment it matters.
/// </para>
/// </remarks>
public sealed record SampleRequirement
{
    /// <summary>Conventional power: an 80% chance of detecting the claimed effect if it is real.</summary>
    public const decimal DefaultPower = 0.80m;

    /// <summary>Conventional one-sided significance.</summary>
    public const decimal DefaultSignificance = 0.05m;

    /// <summary>Beyond this the derivation is reported as unattainable rather than as a number.</summary>
    public const int Unattainable = int.MaxValue;

    private SampleRequirement(
        int floor,
        int derived,
        bool wasDerived,
        decimal claimedBrier,
        decimal? referenceBrier,
        decimal? componentStandardDeviation,
        decimal power,
        decimal significance,
        decimal effectiveSignificance,
        bool claimWasRelative,
        string basis)
    {
        Floor = floor;
        Derived = derived;
        WasDerived = wasDerived;
        ClaimedBrier = claimedBrier;
        ReferenceBrier = referenceBrier;
        ComponentStandardDeviation = componentStandardDeviation;
        Power = power;
        Significance = significance;
        EffectiveSignificance = effectiveSignificance;
        ClaimWasRelative = claimWasRelative;
        Basis = basis;
    }

    /// <summary>The shipped minimum. The requirement never goes below this.</summary>
    public int Floor { get; }

    /// <summary>What the power calculation asked for, or zero when it could not be computed.</summary>
    public int Derived { get; }

    /// <summary>Whether the inputs for a derivation were present at all.</summary>
    public bool WasDerived { get; }

    /// <summary>The Brier score the strategy declared it can achieve.</summary>
    public decimal ClaimedBrier { get; }

    /// <summary>What a forecaster who only knew the base rate would score. Computed, never declared.</summary>
    public decimal? ReferenceBrier { get; }

    /// <summary>The measured spread of the individual Brier components.</summary>
    public decimal? ComponentStandardDeviation { get; }

    public decimal Power { get; }

    public decimal Significance { get; }

    /// <summary>One sentence saying which arm bound and why.</summary>
    public string Basis { get; }

    /// <summary>
    /// The significance the sample was actually sized at, after the multiple-hypothesis correction.
    /// </summary>
    /// <remarks>
    /// <see cref="Significance"/> divided by the number of search families declared against this
    /// evidence base - a Bonferroni correction. Ten independent hypotheses on one dataset raise the
    /// chance that one of them looks good, whether or not any of them was searched for, and the
    /// honest response is a proportionately larger sample rather than a refusal. This is also what
    /// stops the search family being a loophole: a strategy can always open a new family, and doing
    /// so raises the bar for every strategy on that evidence base, including its own.
    /// </remarks>
    public decimal EffectiveSignificance { get; }

    /// <summary>How the claim was stated: an absolute score, or a share of the base rate.</summary>
    public bool ClaimWasRelative { get; }

    /// <summary>The bar actually applied: the floor, or the derivation when it is higher.</summary>
    public int Required => Derived > Floor ? Derived : Floor;

    /// <summary>True when the claim is not better than the base rate, so no sample can establish it.</summary>
    public bool IsUnattainable => Derived == Unattainable;

    /// <summary>
    /// Derives the requirement for one strategy against one measurement.
    /// </summary>
    /// <remarks>
    /// A strategy that declared no claim is read as implicitly claiming it can reach the admission
    /// ceiling, which is the weakest claim that could still be admitted and therefore the most
    /// demanding sample. That is deliberate: not stating a claim must never be the cheap option.
    /// </remarks>
    public static SampleRequirement For(
        StrategyEvent declared,
        StrategyMeasurement measurement,
        AdmissionCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(criteria);

        var floor = criteria.MinimumResolvedPredictions;
        var reference = measurement.ReferenceBrier;
        var spread = measurement.ComponentStandardDeviation;

        // Every hypothesis on one evidence base raises the chance that one of them looks good, so
        // the significance each sample is sized at is divided by how many families exist there.
        var effective = criteria.Significance / measurement.FamiliesOnThisEvidenceBase;

        // A relative claim resolves against the base rate the strategy is actually facing, which is
        // the whole reason it is the better way to claim: (1 - s) x reference is well defined for
        // any event, where an absolute number is only meaningful once the base rate is known.
        var relative = declared.ClaimedSkill is not null;

        var claimed = declared.ClaimedSkill is { } share && reference is { } known
            ? (1m - share) * known
            : declared.ClaimedBrier ?? criteria.MaximumBrierScore;

        if (reference is null || spread is null)
        {
            return new SampleRequirement(
                floor,
                0,
                false,
                claimed,
                reference,
                spread,
                criteria.Power,
                criteria.Significance,
                effective,
                relative,
                "the measurement carried no base rate or no component spread, so no sample size " +
                "could be derived and the shipped floor stands.");
        }

        var gap = reference.Value - claimed;

        if (gap <= 0m)
        {
            return new SampleRequirement(
                floor,
                Unattainable,
                true,
                claimed,
                reference,
                spread,
                criteria.Power,
                criteria.Significance,
                effective,
                relative,
                Invariant($"the claim of {claimed:0.0000} is no better than the base rate's ") +
                Invariant($"{reference.Value:0.0000}. No sample size establishes a claim that ") +
                "carries no information, so the requirement is unattainable rather than large.");
        }

        if (spread.Value <= 0m)
        {
            return new SampleRequirement(
                floor,
                floor,
                true,
                claimed,
                reference,
                spread,
                criteria.Power,
                criteria.Significance,
                effective,
                relative,
                "every prediction scored identically, so the spread is zero and the derivation " +
                "carries no information beyond the floor.");
        }

        var z = StandardNormal.Quantile(1d - (double)effective) +
                StandardNormal.Quantile((double)criteria.Power);

        var n = Math.Pow(z * (double)spread.Value / (double)gap, 2d);

        var derived = n >= Unattainable ? Unattainable : (int)Math.Ceiling(n);

        return new SampleRequirement(
            floor,
            derived,
            true,
            claimed,
            reference,
            spread,
            criteria.Power,
            criteria.Significance,
            effective,
            relative,
            Invariant($"a claim of {claimed:0.0000} ") +
            (relative
                ? Invariant($"(a declared {declared.ClaimedSkill!.Value:P0} of the base rate removed) ")
                : "(declared as an absolute score) ") +
            Invariant($"against a base-rate reference of {reference.Value:0.0000} with a component ") +
            Invariant($"spread of {spread.Value:0.0000} needs {derived} resolved predictions at ") +
            Invariant($"{criteria.Power:P0} power and {effective:0.0000} significance, the shipped ") +
            Invariant($"{criteria.Significance:0.00} divided by the ") +
            Invariant($"{measurement.FamiliesOnThisEvidenceBase} families declared against this ") +
            "evidence base.");
    }

    /// <summary>
    /// How much one observation is worth when observations arrive in correlated clusters.
    /// </summary>
    /// <remarks>
    /// Twenty large caps falling together in one month is one market event seen twenty times, not
    /// twenty events. The standard correction is <c>1 + (m-1)rho</c>, and it is applied here rather
    /// than argued about in a report, because a count that ignores it is a count of rows rather than
    /// of information.
    /// </remarks>
    public static decimal DesignEffect(int observations, int clusters, decimal intraClusterCorrelation)
    {
        if (observations < 0)
        {
            throw new DomainValidationException(
                nameof(observations),
                "A negative count of observations is a defect in whatever counted them.");
        }

        if (clusters < 1)
        {
            throw new DomainValidationException(
                nameof(clusters),
                "Observations fall into at least one cluster. Zero clusters would divide by nothing.");
        }

        if (intraClusterCorrelation is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(intraClusterCorrelation),
                "An intra-cluster correlation lies in [0,1]. Outside it the correction inflates the " +
                "sample instead of discounting it, which is the opposite of the point.");
        }

        if (observations == 0)
        {
            return 1m;
        }

        var meanClusterSize = (decimal)observations / clusters;
        var effect = 1m + ((meanClusterSize - 1m) * intraClusterCorrelation);

        return effect < 1m ? 1m : effect;
    }

    /// <summary>The count discounted for clustering: how many independent observations it is worth.</summary>
    public static int EffectiveSample(int observations, int clusters, decimal intraClusterCorrelation)
    {
        var effect = DesignEffect(observations, clusters, intraClusterCorrelation);

        return (int)Math.Floor(observations / effect);
    }

    public override string ToString() =>
        IsUnattainable
            ? Invariant($"unattainable (floor {Floor})")
            : Invariant($"{Required} resolved predictions (floor {Floor}, derived {Derived})");

    private static string Invariant(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
