using AI.Investment.Domain.Validation;

namespace AI.Investment.Application.Validation;

/// <summary>A number, or the reason there is not one. Never both, and never a bare zero.</summary>
public sealed record MeasurementDto(string Availability, decimal? Value, int SampleSize, string Explanation);

/// <summary>One band of the calibration curve.</summary>
public sealed record CalibrationBinDto(
    decimal LowerRatio,
    decimal UpperRatio,
    int Count,
    MeasurementDto MeanStated,
    MeasurementDto ObservedFrequency,
    MeasurementDto Gap);

/// <summary>What a validation run measured, in a shape a reader can consume over HTTP.</summary>
public sealed record ValidationReportDto(
    Guid RunId,
    DateTime GeneratedAtUtc,
    string Verdict,
    string Conclusion,
    DateTime WindowFromUtc,
    DateTime WindowToUtc,
    TimeSpan Horizon,
    decimal EventThresholdRatio,
    string Methodology,
    string BenchmarkName,
    string BenchmarkFingerprint,
    DateTime BenchmarkDeclaredAtUtc,
    IReadOnlyList<string> DataSources,
    int PredictionsConsidered,
    int PredictionsAdmitted,
    int PredictionsRefused,
    int TruePositives,
    int FalsePositives,
    int TrueNegatives,
    int FalseNegatives,
    int Unresolved,
    int Unavailable,
    int Abstained,
    MeasurementDto HitRate,
    MeasurementDto FalsePositiveRate,
    MeasurementDto FalseNegativeRate,
    MeasurementDto Recall,
    MeasurementDto Accuracy,
    MeasurementDto BrierScore,
    IReadOnlyList<CalibrationBinDto> Calibration,
    MeasurementDto SystemReturn,
    MeasurementDto BenchmarkReturn,
    MeasurementDto ExcessReturn,
    int ShadowMeasurements,
    MeasurementDto ShadowAgreementRate,
    MeasurementDto ShadowDivergenceHitRate,
    IReadOnlyList<string> DataGaps,
    IReadOnlyList<string> Limitations);

/// <summary>Maps validation results onto the transport shapes.</summary>
/// <remarks>
/// The mapping is one-way and lossy on purpose: it flattens for reading and carries no method that
/// turns a DTO back into a report. A report reconstructed from its own presentation would be a
/// measurement whose provenance had been laundered through a JSON payload.
/// </remarks>
public static class ValidationMapper
{
    public static MeasurementDto ToDto(Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        return new MeasurementDto(
            measurement.Availability.ToString(),
            measurement.IsMeasured ? measurement.Value : null,
            measurement.SampleSize,
            measurement.Explanation);
    }

    public static ValidationReportDto ToDto(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ValidationReportDto(
            report.RunId,
            report.GeneratedAtUtc,
            report.Verdict.ToString(),
            report.Conclusion,
            report.Window.FromUtc,
            report.Window.ToUtc,
            report.Window.Horizon,
            report.EventThreshold.Ratio,
            report.Methodology.ToString(),
            report.Benchmark.Name,
            report.Benchmark.Fingerprint,
            report.Benchmark.DeclaredAtUtc,
            report.DataSources,
            report.PredictionsConsidered,
            report.PredictionsAdmitted,
            report.PredictionsRefused,
            report.Matrix.TruePositives,
            report.Matrix.FalsePositives,
            report.Matrix.TrueNegatives,
            report.Matrix.FalseNegatives,
            report.Matrix.Unresolved,
            report.Matrix.Unavailable,
            report.Matrix.Abstained,
            ToDto(report.Matrix.HitRate),
            ToDto(report.Matrix.FalsePositiveRate),
            ToDto(report.Matrix.FalseNegativeRate),
            ToDto(report.Matrix.Recall),
            ToDto(report.Matrix.Accuracy),
            ToDto(report.Calibration.BrierScore),
            report.Calibration.Bins
                .Select(bin => new CalibrationBinDto(
                    bin.LowerRatio,
                    bin.UpperRatio,
                    bin.Count,
                    ToDto(bin.MeanStated),
                    ToDto(bin.ObservedFrequency),
                    ToDto(bin.Gap)))
                .ToList(),
            ToDto(report.SystemReturn),
            ToDto(report.BenchmarkReturn),
            ToDto(report.ExcessReturn),
            report.Shadow.Total,
            ToDto(report.Shadow.AgreementRate),
            ToDto(report.Shadow.DivergenceHitRate),
            report.DataGaps.Select(gap => $"{gap.Metric}: {gap.Reason}").ToList(),
            report.Limitations);
    }
}
