using System.Globalization;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>What a validation run concluded. Deliberately blunt.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero, and <see cref="NotEstablished"/> is the honest verdict when there
/// is not enough evidence - which is a different statement from "no better than the benchmark" and
/// must not be reported as though it were the same. A system that has not been measured is not a
/// system that has been measured and found equal.
/// </remarks>
public enum ValidationVerdict
{
    Unknown = 0,

    /// <summary>Not enough data to say anything. The starting position, and an honest one.</summary>
    NotEstablished = 1,

    /// <summary>Measured, and no better than buying the index.</summary>
    NoBetterThanBenchmark = 2,

    /// <summary>Measured, and worse than buying the index.</summary>
    WorseThanBenchmark = 3,

    /// <summary>Measured, and better than buying the index over this window.</summary>
    BetterThanBenchmark = 4,

    /// <summary>The run failed its own integrity checks. No result is reported at all.</summary>
    RefusedForIntegrity = 5,
}

/// <summary>One thing the data could not support, named so it can be fixed.</summary>
/// <param name="Metric">What could not be measured.</param>
/// <param name="Reason">Why not, in terms of the missing records rather than of the code.</param>
public sealed record DataGap(string Metric, string Reason);

/// <summary>
/// The measured performance report: what was measured, over what, and what it says.
/// </summary>
/// <remarks>
/// <para>
/// This is the deliverable the phase exists to produce. Its exit criterion is that it exists and has
/// been read - not that it is favourable - so the type is built to make an unfavourable or empty
/// result as easy to express as a good one. Every metric is a <see cref="Measurement"/>, the
/// verdict has a value for "not established", and the gaps are a first-class list rather than a
/// footnote.
/// </para>
/// <para>
/// <strong>The methodology travels with the numbers.</strong> Window, horizon, event threshold,
/// benchmark fingerprint and calculation version are all carried here, because a hit rate without
/// them is not a result - it is a number that cannot be checked, reproduced or argued with.
/// </para>
/// </remarks>
public sealed record ValidationReport
{
    public const int MaxTitleLength = 200;

    private ValidationReport(
        Guid runId,
        DateTime generatedAtUtc,
        EvaluationWindow window,
        Percentage eventThreshold,
        CalculationVersion methodology,
        BenchmarkDefinition benchmark,
        IReadOnlyList<string> dataSources,
        int predictionsConsidered,
        int predictionsAdmitted,
        int predictionsRefused,
        ConfusionMatrix matrix,
        CalibrationCurve calibration,
        Measurement systemReturn,
        Measurement benchmarkReturn,
        Measurement excessReturn,
        ShadowComparisonResult shadow,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string> limitations,
        ValidationVerdict verdict,
        string conclusion)
    {
        RunId = runId;
        GeneratedAtUtc = generatedAtUtc;
        Window = window;
        EventThreshold = eventThreshold;
        Methodology = methodology;
        Benchmark = benchmark;
        DataSources = dataSources;
        PredictionsConsidered = predictionsConsidered;
        PredictionsAdmitted = predictionsAdmitted;
        PredictionsRefused = predictionsRefused;
        Matrix = matrix;
        Calibration = calibration;
        SystemReturn = systemReturn;
        BenchmarkReturn = benchmarkReturn;
        ExcessReturn = excessReturn;
        Shadow = shadow;
        DataGaps = gaps;
        Limitations = limitations;
        Verdict = verdict;
        Conclusion = conclusion;
    }

    public Guid RunId { get; }

    public DateTime GeneratedAtUtc { get; }

    public EvaluationWindow Window { get; }

    /// <summary>The realised move at or above which the event counts as having happened.</summary>
    public Percentage EventThreshold { get; }

    public CalculationVersion Methodology { get; }

    public BenchmarkDefinition Benchmark { get; }

    /// <summary>The registered sources the evidence came from.</summary>
    public IReadOnlyList<string> DataSources { get; }

    /// <summary>Predictions the run looked at.</summary>
    public int PredictionsConsidered { get; }

    /// <summary>Predictions that survived the point-in-time guard.</summary>
    public int PredictionsAdmitted { get; }

    /// <summary>Predictions the guard refused, and which therefore appear in no rate.</summary>
    public int PredictionsRefused { get; }

    public ConfusionMatrix Matrix { get; }

    public CalibrationCurve Calibration { get; }

    public Measurement SystemReturn { get; }

    public Measurement BenchmarkReturn { get; }

    /// <summary>System minus benchmark. The headline, when there is one.</summary>
    public Measurement ExcessReturn { get; }

    public ShadowComparisonResult Shadow { get; }

    public IReadOnlyList<DataGap> DataGaps { get; }

    public IReadOnlyList<string> Limitations { get; }

    public ValidationVerdict Verdict { get; }

    public string Conclusion { get; }

    /// <summary>True when the run measured nothing at all. Common, and not a failure.</summary>
    public bool IsEmpty => PredictionsAdmitted == 0;

    public static ValidationReport Create(
        Guid runId,
        DateTime generatedAtUtc,
        EvaluationWindow window,
        Percentage eventThreshold,
        CalculationVersion methodology,
        BenchmarkDefinition benchmark,
        IReadOnlyList<string> dataSources,
        int predictionsConsidered,
        int predictionsAdmitted,
        int predictionsRefused,
        ConfusionMatrix matrix,
        CalibrationCurve calibration,
        Measurement systemReturn,
        Measurement benchmarkReturn,
        ShadowComparisonResult shadow,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string> limitations)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(eventThreshold);
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(dataSources);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(systemReturn);
        ArgumentNullException.ThrowIfNull(benchmarkReturn);
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(limitations);

        DateRange.EnsureUtc(generatedAtUtc, nameof(generatedAtUtc));

        if (predictionsAdmitted < 0 || predictionsRefused < 0 || predictionsConsidered < 0)
        {
            throw new DomainValidationException(
                nameof(predictionsConsidered),
                "A count of predictions cannot be negative, and a report whose arithmetic is wrong " +
                "should not be published at all.");
        }

        if (predictionsAdmitted + predictionsRefused != predictionsConsidered)
        {
            throw new DomainValidationException(
                nameof(predictionsConsidered),
                $"{predictionsAdmitted} admitted plus {predictionsRefused} refused is not " +
                $"{predictionsConsidered} considered. Predictions that are neither admitted nor " +
                "refused have gone missing, and a sample that loses members silently selects itself.");
        }

        var excess = PerformanceCalculator.Excess(systemReturn, benchmarkReturn);
        var verdict = Decide(predictionsAdmitted, excess);

        return new ValidationReport(
            runId,
            generatedAtUtc,
            window,
            eventThreshold,
            methodology,
            benchmark,
            dataSources,
            predictionsConsidered,
            predictionsAdmitted,
            predictionsRefused,
            matrix,
            calibration,
            systemReturn,
            benchmarkReturn,
            excess,
            shadow,
            gaps,
            limitations,
            verdict,
            Conclude(verdict, excess, predictionsAdmitted));
    }

    /// <summary>A report for a run that refused to produce numbers, and why.</summary>
    public static ValidationReport RefusedForIntegrity(
        Guid runId,
        DateTime generatedAtUtc,
        EvaluationWindow window,
        Percentage eventThreshold,
        CalculationVersion methodology,
        BenchmarkDefinition benchmark,
        IReadOnlyList<DataGap> gaps,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var empty = ConfusionMatrix.From([]);

        return new ValidationReport(
            runId,
            generatedAtUtc,
            window,
            eventThreshold,
            methodology,
            benchmark,
            [],
            0,
            0,
            0,
            empty,
            CalibrationCurve.From([]),
            Measurement.Unavailable(reason),
            Measurement.Unavailable(reason),
            Measurement.Unavailable(reason),
            ShadowComparisonResult.From([], new Dictionary<Guid, OutcomeLabel>()),
            gaps,
            ["The run refused to produce numbers. Nothing below should be read as a result."],
            ValidationVerdict.RefusedForIntegrity,
            reason.Trim());
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"validation {RunId:n} over {Window}: {Verdict}");

    private static ValidationVerdict Decide(int admitted, Measurement excess)
    {
        if (admitted == 0 || !excess.IsMeasured)
        {
            return ValidationVerdict.NotEstablished;
        }

        var value = excess.Value!.Value;

        // Equal is not "better". The comparison has to be won on the evidence, and a difference
        // indistinguishable from zero is reported as no better rather than as a narrow victory.
        return value switch
        {
            > 0m => ValidationVerdict.BetterThanBenchmark,
            < 0m => ValidationVerdict.WorseThanBenchmark,
            _ => ValidationVerdict.NoBetterThanBenchmark,
        };
    }

    private static string Conclude(ValidationVerdict verdict, Measurement excess, int admitted) =>
        verdict switch
        {
            ValidationVerdict.NotEstablished when admitted == 0 =>
                "No prediction survived the point-in-time guard over this window, so nothing was " +
                "measured. The platform's central claim - that it produces useful analysis - remains " +
                "an untested hypothesis, and this report is the record of that rather than a result.",

            ValidationVerdict.NotEstablished =>
                $"{admitted} predictions were admitted, but the comparison against the benchmark " +
                "could not be completed. No claim about performance is made.",

            ValidationVerdict.BetterThanBenchmark =>
                $"Over this window the system returned {excess.Value:P2} more than buying and holding " +
                "the benchmark. One window is not a track record, and this is not evidence that it " +
                "will do so again.",

            ValidationVerdict.WorseThanBenchmark =>
                $"Over this window the system returned {excess.Value:P2} less than buying and holding " +
                "the benchmark. On this evidence the analysis did not pay for itself.",

            ValidationVerdict.NoBetterThanBenchmark =>
                "Over this window the system matched buying and holding the benchmark. The analysis " +
                "added nothing that the simplest possible alternative did not.",

            ValidationVerdict.RefusedForIntegrity =>
                "The run refused to produce numbers because its own integrity checks failed.",

            ValidationVerdict.Unknown => "No verdict was reached.",

            _ => "No verdict was reached.",
        };
}
