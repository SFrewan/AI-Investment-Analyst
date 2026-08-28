using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Validation;

/// <summary>One band of stated probability, and what actually happened inside it.</summary>
/// <param name="LowerRatio">Inclusive lower bound of the band.</param>
/// <param name="UpperRatio">Upper bound, inclusive only for the topmost band.</param>
/// <param name="Count">Resolved predictions that fell in it.</param>
/// <param name="MeanStated">The average probability the system claimed inside the band.</param>
/// <param name="ObservedFrequency">The share that actually happened, when there are enough to say.</param>
public sealed record CalibrationBin(
    decimal LowerRatio,
    decimal UpperRatio,
    int Count,
    Measurement MeanStated,
    Measurement ObservedFrequency)
{
    /// <summary>How far the band's claim was from reality. Positive means overconfident.</summary>
    public Measurement Gap =>
        MeanStated.IsMeasured && ObservedFrequency.IsMeasured
            ? Measurement.Measured(
                MeanStated.Value!.Value - ObservedFrequency.Value!.Value,
                Count,
                "stated minus observed; positive is overconfident")
            : Measurement.Unavailable(
                "the band does not have enough resolved predictions to compare its claim with reality.");

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{LowerRatio:0.0}-{UpperRatio:0.0}) n={Count}");
}

/// <summary>
/// Whether the system's stated probabilities mean anything.
/// </summary>
/// <remarks>
/// <para>
/// Calibration is the question a hit rate cannot answer. A system that says "seventy per cent" and is
/// right seventy per cent of the time is useful even if it is often wrong; a system that says
/// "ninety-five per cent" and is right sixty per cent of the time is dangerous precisely because it
/// is confident, and no accuracy figure reveals that. The curve is the comparison, band by band.
/// </para>
/// <para>
/// <strong>Bands are declared, not discovered.</strong> The bin edges are fixed before the run, in
/// this type, rather than chosen from the data - because bins fitted to a sample can make almost any
/// set of predictions look calibrated, and the resulting curve is a picture of the binning.
/// </para>
/// <para>
/// Each band reports its own availability. A band with three predictions in it says so instead of
/// printing a frequency of one third, which would be the least reliable number in the report and
/// would look exactly like the most confident one.
/// </para>
/// </remarks>
public sealed record CalibrationCurve
{
    /// <summary>Ten fixed bands, declared here and never fitted to a sample.</summary>
    public const int BinCount = 10;

    /// <summary>Below this, a band's observed frequency is withheld.</summary>
    public const int MinimumPerBin = 10;

    /// <summary>Below this, the whole curve is withheld.</summary>
    public const int MinimumSample = 20;

    private CalibrationCurve(
        IReadOnlyList<CalibrationBin> bins,
        Measurement brierScore,
        int resolvedCount,
        int withoutStatedProbability)
    {
        Bins = bins;
        BrierScore = brierScore;
        ResolvedCount = resolvedCount;
        WithoutStatedProbability = withoutStatedProbability;
    }

    public IReadOnlyList<CalibrationBin> Bins { get; }

    /// <summary>
    /// Mean squared error between stated probability and outcome. Zero is perfect; 0.25 is what a
    /// system that says "fifty per cent" to everything scores, and is the number to beat.
    /// </summary>
    public Measurement BrierScore { get; }

    /// <summary>Resolved predictions that carried a stated probability.</summary>
    public int ResolvedCount { get; }

    /// <summary>Resolved predictions with no stated probability, which cannot be calibrated at all.</summary>
    public int WithoutStatedProbability { get; }

    /// <summary>
    /// Builds the curve from resolved predictions.
    /// </summary>
    /// <param name="samples">
    /// Stated probability as a ratio in [0,1], and whether the event occurred. Only resolved
    /// predictions belong here: an unresolved one has no second element to supply.
    /// </param>
    public static CalibrationCurve From(
        IEnumerable<(decimal StatedRatio, bool Occurred)> samples,
        int withoutStatedProbability = 0,
        int minimumSample = MinimumSample,
        int minimumPerBin = MinimumPerBin)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (minimumSample < 1 || minimumPerBin < 1)
        {
            throw new DomainValidationException(
                nameof(minimumSample),
                "A minimum of zero would let a single observation be reported as a calibration curve.");
        }

        var material = samples.ToList();

        foreach (var sample in material)
        {
            if (sample.StatedRatio is < 0m or > 1m)
            {
                throw new DomainValidationException(
                    nameof(samples),
                    $"A stated probability of {sample.StatedRatio} is not a probability. Calibration " +
                    "compares claims against reality, and a claim outside [0,1] is a defect rather " +
                    "than an overconfident forecast.");
            }
        }

        var bins = new List<CalibrationBin>(BinCount);

        for (var index = 0; index < BinCount; index++)
        {
            var lower = index / (decimal)BinCount;
            var upper = (index + 1) / (decimal)BinCount;
            var isTop = index == BinCount - 1;

            var inBin = material
                .Where(sample => sample.StatedRatio >= lower &&
                    (isTop ? sample.StatedRatio <= upper : sample.StatedRatio < upper))
                .ToList();

            bins.Add(new CalibrationBin(
                lower,
                upper,
                inBin.Count,
                Mean(inBin.Select(s => s.StatedRatio).ToList(), minimumPerBin, "mean stated probability"),
                Mean(inBin.Select(s => s.Occurred ? 1m : 0m).ToList(), minimumPerBin, "observed frequency")));
        }

        var brier = material.Count == 0
            ? Measurement.Unavailable(
                "no resolved prediction carried a stated probability, so there is nothing to score.")
            : material.Count < minimumSample
                ? Measurement.Insufficient(material.Count, minimumSample)
                : Measurement.Measured(
                    material.Sum(s => Square(s.StatedRatio - (s.Occurred ? 1m : 0m))) / material.Count,
                    material.Count,
                    "Brier score; 0 is perfect and 0.25 is what always saying fifty per cent scores");

        return new CalibrationCurve(bins, brier, material.Count, Math.Max(withoutStatedProbability, 0));
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"calibration over {ResolvedCount} resolved predictions");

    private static decimal Square(decimal value) => value * value;

    private static Measurement Mean(List<decimal> values, int minimum, string name)
    {
        if (values.Count == 0)
        {
            return Measurement.Unavailable($"{name}: the band is empty.");
        }

        return values.Count < minimum
            ? Measurement.Insufficient(values.Count, minimum)
            : Measurement.Measured(values.Sum() / values.Count, values.Count, name);
    }
}
