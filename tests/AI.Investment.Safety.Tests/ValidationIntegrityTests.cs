using System.Reflection;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The integrity rules of a measurement: it cannot invent numbers, and it cannot act.
/// </summary>
/// <remarks>
/// <para>
/// Validation is the phase most exposed to a particular kind of dishonesty, and it is not usually
/// deliberate. A window quietly widened, a benchmark quietly swapped, a rate printed over four
/// observations, an absence rendered as a zero: each is a small convenience that produces a report
/// saying something the data does not. These tests are the structural refusals that make those
/// conveniences fail loudly.
/// </para>
/// <para>
/// The second half is the autonomy boundary. Phase 7 reads Phase 6's shadow records and counts them.
/// Nothing here may execute, and the prohibition is asserted over the built assemblies rather than
/// asserted in a comment.
/// </para>
/// </remarks>
public sealed class ValidationIntegrityTests
{
    private static readonly DateTime Generated = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Index = IngestionSubject.Create("Security", "SPY");

    private static EvaluationWindow Window() =>
        EvaluationWindow.Create(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(1));

    private static BenchmarkDefinition Benchmark() =>
        BenchmarkDefinition.Create(
            "index buy-and-hold",
            Index,
            "security.close",
            BenchmarkRule.BuyAndHold,
            Money.Create(100_000m, Currency.Usd),
            Percentage.Zero,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    // ---- a report cannot lose predictions -----------------------------------------------------

    /// <summary>
    /// Admitted plus refused must equal considered. A sample that loses members selects itself, and
    /// every rate computed over it afterwards is over a subset nobody chose on purpose.
    /// </summary>
    [Fact]
    public void A_report_whose_prediction_counts_do_not_add_up_cannot_be_created()
    {
        var error = Assert.Throws<DomainValidationException>(() => Report(considered: 10, admitted: 4, refused: 3));

        Assert.Contains("gone missing", error.Message, StringComparison.Ordinal);

        // The consistent version is accepted, so the rule is the arithmetic rather than the shape.
        _ = Report(considered: 10, admitted: 4, refused: 6);
    }

    [Fact]
    public void A_report_cannot_be_created_with_negative_counts()
    {
        Assert.Throws<DomainValidationException>(() => Report(considered: -1, admitted: 0, refused: 0));
    }

    /// <summary>
    /// Equal is not better. A difference indistinguishable from zero is reported as no better rather
    /// than as a narrow win.
    /// </summary>
    [Fact]
    public void Matching_the_benchmark_is_reported_as_no_better_than_it()
    {
        var report = Report(
            considered: 40,
            admitted: 40,
            refused: 0,
            system: Measurement.Measured(0.10m, 40, "system"),
            benchmark: Measurement.Measured(0.10m, 40, "benchmark"));

        Assert.Equal(ValidationVerdict.NoBetterThanBenchmark, report.Verdict);
        Assert.Contains("added nothing", report.Conclusion, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Not established" and "no better than the benchmark" are different findings, and a system that
    /// has not been measured must never be reported as one that was measured and found equal.
    /// </summary>
    [Fact]
    public void An_unmeasured_run_is_not_established_rather_than_equal_to_the_benchmark()
    {
        var report = Report(considered: 0, admitted: 0, refused: 0);

        Assert.Equal(ValidationVerdict.NotEstablished, report.Verdict);
        Assert.NotEqual(ValidationVerdict.NoBetterThanBenchmark, report.Verdict);
        Assert.True(report.IsEmpty);
    }

    /// <summary>A run that refused to measure reports no numbers at all, and says why.</summary>
    [Fact]
    public void A_run_refused_for_integrity_reports_no_numbers()
    {
        var report = ValidationReport.RefusedForIntegrity(
            Guid.NewGuid(),
            Generated,
            Window(),
            Percentage.Zero,
            CalculationVersion.Create(1, 0),
            Benchmark(),
            [new DataGap("everything", "the history could not be established")],
            "the run failed its own integrity checks");

        Assert.Equal(ValidationVerdict.RefusedForIntegrity, report.Verdict);
        Assert.False(report.SystemReturn.IsMeasured);
        Assert.False(report.BenchmarkReturn.IsMeasured);
        Assert.False(report.ExcessReturn.IsMeasured);
        Assert.False(report.Matrix.HitRate.IsMeasured);
        Assert.Equal(0, report.Matrix.Total);
        Assert.Contains(report.Limitations, limit => limit.Contains("should be read as a result", StringComparison.Ordinal));
    }

    // ---- the rendered report cannot show an absence as a number --------------------------------

    /// <summary>
    /// The renderer has no way to turn an unmeasured metric into a figure: the value is not there to
    /// print. Asserted on the output, because that is what a reader sees.
    /// </summary>
    [Fact]
    public void An_empty_report_renders_reasons_rather_than_zeroes()
    {
        var markdown = ValidationReportWriter.ToMarkdown(Report(considered: 0, admitted: 0, refused: 0));

        Assert.Contains("| Hit rate (precision) | _", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Hit rate (precision) | 0.00%", markdown, StringComparison.Ordinal);
        Assert.Contains("Autonomy is unchanged by this phase and remains **L3**.", markdown, StringComparison.Ordinal);
        Assert.Contains("Retrieval time is never used to admit anything", markdown, StringComparison.Ordinal);
        Assert.Contains("Nothing in this report is investment advice", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The benchmark's fingerprint travels into the report, so the comparison described can be shown
    /// to be the comparison used.
    /// </summary>
    [Fact]
    public void The_rendered_report_carries_the_benchmark_fingerprint_and_declaration_date()
    {
        var benchmark = Benchmark();
        var markdown = ValidationReportWriter.ToMarkdown(Report(0, 0, 0));

        Assert.Contains(benchmark.Fingerprint, markdown, StringComparison.Ordinal);
        Assert.Contains(benchmark.DeclaredAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            markdown, StringComparison.Ordinal);
    }

    // ---- validation cannot act -----------------------------------------------------------------

    /// <summary>
    /// Nothing in the validation namespaces takes an effect, a gateway or a venue. The measurement
    /// path has no way to reach an execution even by mistake, which is the same guarantee Phase 6's
    /// shadow evaluator has and for the same reason.
    /// </summary>
    [Fact]
    public void No_validation_type_can_reach_an_execution()
    {
        var forbidden = new[] { "IActionGateway", "IExecutionVenue", "IWriteAuthorization", "ActionExecution" };

        var types = typeof(ValidationReport).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("AI.Investment.Domain.Validation", StringComparison.Ordinal) == true)
            .Concat(typeof(ValidationService).Assembly.GetTypes()
                .Where(type => type.Namespace?.StartsWith("AI.Investment.Application.Validation", StringComparison.Ordinal) == true))
            .ToList();

        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            var signatures = type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .OfType<MethodBase>()
                .SelectMany(method => method.GetParameters())
                .Select(parameter => parameter.ParameterType.Name)
                .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType.Name))
                .ToList();

            foreach (var name in forbidden)
            {
                Assert.DoesNotContain(name, signatures, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The shadow comparison is a counting function over records. It takes no delegate it could
    /// invoke and returns a value rather than a task, so there is nothing for it to do but count.
    /// </summary>
    [Fact]
    public void The_shadow_comparison_is_arithmetic_and_nothing_else()
    {
        var method = typeof(ShadowComparisonResult).GetMethod(nameof(ShadowComparisonResult.From));

        Assert.NotNull(method);
        Assert.Equal(typeof(ShadowComparisonResult), method!.ReturnType);

        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    private static ValidationReport Report(
        int considered,
        int admitted,
        int refused,
        Measurement? system = null,
        Measurement? benchmark = null) =>
        ValidationReport.Create(
            Guid.NewGuid(),
            Generated,
            Window(),
            Percentage.Zero,
            CalculationVersion.Create(1, 0),
            Benchmark(),
            [],
            considered,
            admitted,
            refused,
            ConfusionMatrix.From([]),
            CalibrationCurve.From([]),
            system ?? Measurement.Unavailable("the strategy took no positions."),
            benchmark ?? Measurement.Unavailable("no benchmark prices."),
            ShadowComparisonResult.From([], new Dictionary<Guid, OutcomeLabel>()),
            [],
            []);
}
