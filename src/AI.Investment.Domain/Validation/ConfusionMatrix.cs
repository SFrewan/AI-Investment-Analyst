using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>
/// The four ways a call can turn out, plus the three ways it can fail to be a call at all.
/// </summary>
/// <remarks>
/// <para>
/// The excluded counts are carried alongside the four cells rather than discarded, because the ratio
/// between them is itself a result. A system that abstained on nine hundred of a thousand
/// opportunities and was right on ninety of the remaining hundred has a hit rate of ninety per cent
/// and has told you almost nothing, and only the excluded counts make that visible.
/// </para>
/// <para>
/// <strong>Hit rate is precision, and this is the only place that says so.</strong> Of the calls the
/// system made to act, the share that turned out right: <c>TP / (TP + FP)</c>. It is not accuracy -
/// which a system that abstains from everything can drive arbitrarily high - and it is not recall.
/// All three are exposed, each named for what it is, so a reader is never left guessing which
/// definition produced the headline number.
/// </para>
/// <para>
/// Every rate is a <see cref="Measurement"/>, so a matrix built from too few observations returns
/// the sample size and a refusal rather than a percentage.
/// </para>
/// </remarks>
public sealed record ConfusionMatrix
{
    /// <summary>Below this, a rate is arithmetic rather than evidence.</summary>
    public const int MinimumSample = 20;

    private ConfusionMatrix(
        int truePositives,
        int falsePositives,
        int trueNegatives,
        int falseNegatives,
        int unresolved,
        int unavailable,
        int abstained,
        int minimumSample)
    {
        TruePositives = truePositives;
        FalsePositives = falsePositives;
        TrueNegatives = trueNegatives;
        FalseNegatives = falseNegatives;
        Unresolved = unresolved;
        Unavailable = unavailable;
        Abstained = abstained;
        MinimumSampleSize = minimumSample;
    }

    public int TruePositives { get; }

    public int FalsePositives { get; }

    public int TrueNegatives { get; }

    public int FalseNegatives { get; }

    /// <summary>Predictions whose horizon has not elapsed.</summary>
    public int Unresolved { get; }

    /// <summary>Predictions with no outcome data.</summary>
    public int Unavailable { get; }

    /// <summary>Predictions where the system declined to call it.</summary>
    public int Abstained { get; }

    public int MinimumSampleSize { get; }

    /// <summary>Predictions that were scored. The denominator of accuracy.</summary>
    public int Scored => TruePositives + FalsePositives + TrueNegatives + FalseNegatives;

    /// <summary>Everything that entered the measurement, scored or not.</summary>
    public int Total => Scored + Unresolved + Unavailable + Abstained;

    /// <summary>Calls to act. The denominator of the hit rate.</summary>
    public int PositiveCalls => TruePositives + FalsePositives;

    /// <summary>Occasions on which the event actually occurred. The denominator of recall.</summary>
    public int ActualPositives => TruePositives + FalseNegatives;

    /// <summary>
    /// The share of calls to act that turned out right. This is the hit rate, and nothing else is.
    /// </summary>
    public Measurement HitRate => Rate(TruePositives, PositiveCalls, "hit rate (precision)");

    /// <summary>The share of occasions the system caught. Its blind spot, seen from the other side.</summary>
    public Measurement Recall => Rate(TruePositives, ActualPositives, "recall");

    /// <summary>The share of all scored predictions that were right, calls and abstentions alike.</summary>
    public Measurement Accuracy =>
        Rate(TruePositives + TrueNegatives, Scored, "accuracy");

    /// <summary>The share of calls to act that were wrong.</summary>
    public Measurement FalsePositiveRate => Rate(FalsePositives, PositiveCalls, "false positive rate");

    /// <summary>The share of real occasions that were missed.</summary>
    public Measurement FalseNegativeRate => Rate(FalseNegatives, ActualPositives, "false negative rate");

    /// <summary>Counts the labels. The only way to build one.</summary>
    public static ConfusionMatrix From(IEnumerable<OutcomeLabel> labels, int minimumSample = MinimumSample)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (minimumSample < 1)
        {
            throw new DomainValidationException(
                nameof(minimumSample),
                "A minimum sample size of zero would let any rate be reported, which is the thing " +
                "this parameter exists to prevent.");
        }

        int tp = 0, fp = 0, tn = 0, fn = 0, unresolved = 0, unavailable = 0, abstained = 0;

        foreach (var label in labels)
        {
            switch (label)
            {
                case OutcomeLabel.TruePositive: tp++; break;
                case OutcomeLabel.FalsePositive: fp++; break;
                case OutcomeLabel.TrueNegative: tn++; break;
                case OutcomeLabel.FalseNegative: fn++; break;
                case OutcomeLabel.Unresolved: unresolved++; break;
                case OutcomeLabel.Unavailable: unavailable++; break;
                case OutcomeLabel.Abstained: abstained++; break;

                case OutcomeLabel.Unknown:
                default:
                    // An unlabelled prediction is a defect in the pipeline, not a result. It is
                    // counted as unavailable so that it shows up in the total rather than vanishing
                    // from the denominator, which is how a sample quietly selects itself.
                    unavailable++;
                    break;
            }
        }

        return new ConfusionMatrix(tp, fp, tn, fn, unresolved, unavailable, abstained, minimumSample);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"TP={TruePositives} FP={FalsePositives} TN={TrueNegatives} FN={FalseNegatives} " +
            $"(unresolved={Unresolved}, unavailable={Unavailable}, abstained={Abstained})");

    private Measurement Rate(int numerator, int denominator, string name)
    {
        if (denominator == 0)
        {
            return Measurement.Unavailable(
                $"{name}: there were no observations in its denominator, so the question it answers " +
                "was never put to the system.");
        }

        return denominator < MinimumSampleSize
            ? Measurement.Insufficient(denominator, MinimumSampleSize)
            : Measurement.Measured((decimal)numerator / denominator, denominator, name);
    }
}

/// <summary>Convenience for reading a rate as a percentage without repeating the conversion.</summary>
public static class RateExtensions
{
    public static Percentage? AsPercentage(this Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        return measurement.IsMeasured ? Percentage.FromRatio(measurement.Value!.Value) : null;
    }
}
