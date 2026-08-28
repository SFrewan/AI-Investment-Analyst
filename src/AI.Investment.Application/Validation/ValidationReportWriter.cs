using System.Globalization;
using System.Text;
using AI.Investment.Domain.Validation;

namespace AI.Investment.Application.Validation;

/// <summary>
/// Renders a validation report as Markdown, in the order a sceptical reader needs it.
/// </summary>
/// <remarks>
/// <para>
/// Methodology first, conclusion last. A report that opens with a headline number invites the reader
/// to stop there, and the headline is the least reliable thing in it: whether it means anything
/// depends entirely on the window, the threshold, the sample and the gaps, all of which come first
/// here for that reason.
/// </para>
/// <para>
/// <strong>An unmeasured metric prints its reason, never a zero.</strong> Every number in the output
/// comes from a <see cref="Measurement"/>, so the renderer has no way to turn an absence into a
/// figure even by accident - the value is simply not there to print.
/// </para>
/// </remarks>
public static class ValidationReportWriter
{
    public static string ToMarkdown(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();

        Header(text, report);
        Methodology(text, report);
        Sample(text, report);
        Rates(text, report);
        Calibration(text, report);
        Benchmark(text, report);
        Shadow(text, report);
        Gaps(text, report);
        Conclusion(text, report);

        return text.ToString();
    }

    private static void Header(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("# Validation report");
        text.AppendLine();
        text.AppendLine(Invariant($"**Run:** `{report.RunId:n}`  "));
        text.AppendLine(Invariant($"**Generated:** {report.GeneratedAtUtc:O}  "));
        text.AppendLine(Invariant($"**Verdict:** {Describe(report.Verdict)}"));
        text.AppendLine();
        text.AppendLine(
            "This report measures the platform. It does not tune it: no threshold, model or ranking " +
            "was adjusted from anything below, and the benchmark was fixed before the run began.");
        text.AppendLine();
        text.AppendLine("Autonomy is unchanged by this phase and remains **L3**.");
        text.AppendLine();
    }

    private static void Methodology(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 1. Methodology");
        text.AppendLine();
        text.AppendLine("| Item | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine(Invariant($"| Evaluation period | {report.Window.FromUtc:O} to {report.Window.ToUtc:O} |"));
        text.AppendLine(Invariant($"| Horizon | {report.Window.Horizon} |"));
        text.AppendLine(Invariant($"| Step | {report.Window.Step} |"));
        text.AppendLine(Invariant($"| Event threshold | a realised move at or above {report.EventThreshold} |"));
        text.AppendLine(Invariant($"| Method version | {report.Methodology} |"));
        text.AppendLine(Invariant($"| Benchmark | {report.Benchmark.Name} — {report.Benchmark.Rule} of {report.Benchmark.Subject} |"));
        text.AppendLine(Invariant($"| Benchmark declared | {report.Benchmark.DeclaredAtUtc:O} |"));
        text.AppendLine(Invariant($"| Benchmark fingerprint | `{report.Benchmark.Fingerprint}` |"));
        text.AppendLine(Invariant($"| Trading cost | {report.Benchmark.CostPerTrade} per leg, charged to both sides |"));
        text.AppendLine();
        text.AppendLine("### Point-in-time rule");
        text.AppendLine();
        text.AppendLine(
            "A value is admissible at a decision only if it became **public** at or before that " +
            "decision, judged on `Provenance.PublishedAtUtc`. Retrieval time is never used to admit " +
            "anything: it records when this installation happened to fetch a value, so admitting on " +
            "it would make a historical result change when a source is backfilled. A value whose " +
            "publication time cannot be established is excluded rather than assumed sound, and a " +
            "derived value is admissible only if every input behind it was.");
        text.AppendLine();
        text.AppendLine("### Data sources");
        text.AppendLine();

        if (report.DataSources.Count == 0)
        {
            text.AppendLine("None. No observation from any registered source falls in this window.");
        }
        else
        {
            foreach (var source in report.DataSources)
            {
                text.AppendLine(Invariant($"- `{source}`"));
            }
        }

        text.AppendLine();
    }

    private static void Sample(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 2. Sample");
        text.AppendLine();
        text.AppendLine("| | Count |");
        text.AppendLine("|---|---:|");
        text.AppendLine(Invariant($"| Predictions considered | {report.PredictionsConsidered} |"));
        text.AppendLine(Invariant($"| Admitted by the point-in-time guard | {report.PredictionsAdmitted} |"));
        text.AppendLine(Invariant($"| Refused by the point-in-time guard | {report.PredictionsRefused} |"));
        text.AppendLine(Invariant($"| Scored | {report.Matrix.Scored} |"));
        text.AppendLine(Invariant($"| Unresolved (horizon not elapsed) | {report.Matrix.Unresolved} |"));
        text.AppendLine(Invariant($"| Unavailable (no outcome data) | {report.Matrix.Unavailable} |"));
        text.AppendLine(Invariant($"| Abstained (no call made) | {report.Matrix.Abstained} |"));
        text.AppendLine();
    }

    private static void Rates(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 3. Hit rate, false positives and false negatives");
        text.AppendLine();
        text.AppendLine(
            "**Hit rate here means precision**: of the calls the system made to act, the share that " +
            "turned out right. It is not accuracy, which a system that abstains from everything can " +
            "drive arbitrarily high.");
        text.AppendLine();
        text.AppendLine("| Cell | Count |");
        text.AppendLine("|---|---:|");
        text.AppendLine(Invariant($"| True positives | {report.Matrix.TruePositives} |"));
        text.AppendLine(Invariant($"| False positives | {report.Matrix.FalsePositives} |"));
        text.AppendLine(Invariant($"| True negatives | {report.Matrix.TrueNegatives} |"));
        text.AppendLine(Invariant($"| False negatives | {report.Matrix.FalseNegatives} |"));
        text.AppendLine();
        text.AppendLine("| Metric | Result |");
        text.AppendLine("|---|---|");
        text.AppendLine(Invariant($"| Hit rate (precision) | {Ratio(report.Matrix.HitRate)} |"));
        text.AppendLine(Invariant($"| False positive rate | {Ratio(report.Matrix.FalsePositiveRate)} |"));
        text.AppendLine(Invariant($"| False negative rate | {Ratio(report.Matrix.FalseNegativeRate)} |"));
        text.AppendLine(Invariant($"| Recall | {Ratio(report.Matrix.Recall)} |"));
        text.AppendLine(Invariant($"| Accuracy | {Ratio(report.Matrix.Accuracy)} |"));
        text.AppendLine();
    }

    private static void Calibration(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 4. Calibration");
        text.AppendLine();
        text.AppendLine(
            "Whether the stated probabilities mean anything. A well-calibrated system that says " +
            "seventy per cent is right about seventy per cent of the time; an overconfident one is " +
            "dangerous in proportion to how sure it sounds.");
        text.AppendLine();
        text.AppendLine(Invariant($"**Brier score:** {Number(report.Calibration.BrierScore)} "));
        text.AppendLine("(0 is perfect; 0.25 is what always saying fifty per cent scores, and is the number to beat.)");
        text.AppendLine();
        text.AppendLine(
            Invariant($"Resolved predictions carrying a stated probability: {report.Calibration.ResolvedCount}. ") +
            Invariant($"Resolved without one, and therefore uncalibratable: {report.Calibration.WithoutStatedProbability}."));
        text.AppendLine();
        text.AppendLine("| Stated band | n | Mean stated | Observed | Gap |");
        text.AppendLine("|---|---:|---|---|---|");

        foreach (var bin in report.Calibration.Bins)
        {
            text.AppendLine(
                Invariant($"| {bin.LowerRatio:0.0}-{bin.UpperRatio:0.0} | {bin.Count} | ") +
                Invariant($"{Ratio(bin.MeanStated)} | {Ratio(bin.ObservedFrequency)} | {Ratio(bin.Gap)} |"));
        }

        text.AppendLine();
    }

    private static void Benchmark(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 5. Against the benchmark");
        text.AppendLine();
        text.AppendLine(
            "Both sides are priced by the same function, over the same window, with the same cost " +
            "model. Returns are simple and equal-weighted across round trips rather than compounded.");
        text.AppendLine();
        text.AppendLine("| | Return |");
        text.AppendLine("|---|---|");
        text.AppendLine(Invariant($"| System | {Ratio(report.SystemReturn)} |"));
        text.AppendLine(Invariant($"| Benchmark ({report.Benchmark.Name}) | {Ratio(report.BenchmarkReturn)} |"));
        text.AppendLine(Invariant($"| **Excess** | {Ratio(report.ExcessReturn)} |"));
        text.AppendLine();
    }

    private static void Shadow(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 6. Shadow versus actual");
        text.AppendLine();
        text.AppendLine(
            "Phase 6 recorded, for every gated action, what the same policy engine would have answered " +
            "one autonomy level higher. Nothing here executed anything then and nothing does now.");
        text.AppendLine();
        text.AppendLine("| | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine(Invariant($"| Measurements in window | {report.Shadow.Total} |"));
        text.AppendLine(Invariant($"| Agreements | {report.Shadow.Agreements} |"));
        text.AppendLine(Invariant($"| Divergences | {report.Shadow.DivergenceCount} |"));
        text.AppendLine(Invariant($"| Would have acted where the platform did not | {report.Shadow.ShadowWouldHaveExecutedAndActualDidNot} |"));
        text.AppendLine(Invariant($"| Platform acted where a higher level would not | {report.Shadow.ActualExecutedAndShadowWouldNot} |"));
        text.AppendLine(Invariant($"| Agreement rate | {Ratio(report.Shadow.AgreementRate)} |"));
        text.AppendLine(Invariant($"| Hit rate of the extra actions | {Ratio(report.Shadow.DivergenceHitRate)} |"));
        text.AppendLine();
        text.AppendLine(
            "Only the last row bears on whether autonomy should rise. \"A higher level would have " +
            "acted more often\" describes the policy, not the quality of the decisions.");
        text.AppendLine();
    }

    private static void Gaps(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 7. Data gaps and limitations");
        text.AppendLine();

        if (report.DataGaps.Count == 0)
        {
            text.AppendLine("No data gap was recorded for this run.");
        }
        else
        {
            foreach (var gap in report.DataGaps)
            {
                text.AppendLine(Invariant($"- **{gap.Metric}** — {gap.Reason}"));
            }
        }

        text.AppendLine();

        foreach (var limitation in report.Limitations)
        {
            text.AppendLine(Invariant($"- {limitation}"));
        }

        text.AppendLine();
    }

    private static void Conclusion(StringBuilder text, ValidationReport report)
    {
        text.AppendLine("## 8. Conclusion");
        text.AppendLine();
        text.AppendLine(report.Conclusion);
        text.AppendLine();
        text.AppendLine(
            "Nothing in this report is investment advice, and nothing in it should be read as a " +
            "prediction of future returns.");
        text.AppendLine();
    }

    private static string Describe(ValidationVerdict verdict) => verdict switch
    {
        ValidationVerdict.NotEstablished => "**not established** — there is not enough evidence to say",
        ValidationVerdict.NoBetterThanBenchmark => "**no better than the benchmark**",
        ValidationVerdict.WorseThanBenchmark => "**worse than the benchmark**",
        ValidationVerdict.BetterThanBenchmark => "**better than the benchmark over this window**",
        ValidationVerdict.RefusedForIntegrity => "**refused** — the run failed its own integrity checks",
        ValidationVerdict.Unknown => "**unknown**",
        _ => "**unknown**",
    };

    private static string Ratio(Measurement measurement) =>
        measurement.IsMeasured
            ? Invariant($"{measurement.Value!.Value:P2} (n={measurement.SampleSize})")
            : Invariant($"_{measurement.Explanation}_");

    private static string Number(Measurement measurement) =>
        measurement.IsMeasured
            ? Invariant($"{measurement.Value!.Value:0.0000} (n={measurement.SampleSize})")
            : Invariant($"_{measurement.Explanation}_");

    private static string Invariant(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
