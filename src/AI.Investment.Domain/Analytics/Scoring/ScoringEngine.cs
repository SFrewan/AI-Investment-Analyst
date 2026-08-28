using System.Globalization;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics.Scoring;

/// <summary>
/// Combines measurements into one comparable number, exactly as its specification declares.
/// </summary>
/// <remarks>
/// <para>
/// A scoring engine is itself a calculator: its inputs are other measurements, and its output is a
/// <see cref="MetricResult"/> like any other. That is not a convenience - it means a score inherits
/// the whole apparatus already built for measurements. Its evidence chain runs back through each
/// component's own inputs to the filings underneath, its own look-ahead guard applies, it carries a
/// version, and it enters the epistemic model as a Calculation rather than as a fact.
/// </para>
/// <para>
/// No model is involved and none may be. A score is arithmetic over a declared specification; if a
/// judgement is wanted about what a score means, that is a separate claim of a different kind, made
/// somewhere else and marked as such.
/// </para>
/// </remarks>
public sealed class ScoringEngine : IMetricCalculator<IReadOnlyCollection<MetricResult>>
{
    public ScoringEngine(ScoringSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        Specification = specification;
        CalculatorId = SourceId.Create($"calc.{specification.Score.Value}");
    }

    public ScoringSpecification Specification { get; }

    public MetricId Metric => Specification.Score;

    public SourceId CalculatorId { get; }

    public CalculationVersion Version => Specification.Version;

    /// <summary>A score is a proportion of the best it could have been.</summary>
    public UnitOfMeasure Unit => UnitOfMeasure.Ratio;

    public CalculationOutcome Calculate(CalculationContext context, IReadOnlyCollection<MetricResult> inputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);

        var duplicated = inputs
            .GroupBy(result => result.Metric.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicated.Count > 0)
        {
            return CalculationOutcome.InsufficientData(
                Metric,
                InsufficientDataReason.ConflictingEvidence,
                $"More than one measurement was supplied for: {string.Join(", ", duplicated)}. " +
                "Choosing between them is not a decision arithmetic may make.");
        }

        var matched = new List<(ScoreComponent Component, MetricResult Result)>();

        foreach (var component in Specification.Components)
        {
            var result = inputs.FirstOrDefault(
                candidate => candidate.Metric.Equals(component.Metric));

            if (result is null)
            {
                continue;
            }

            CalculationGuards.EnsureSubject(Metric, context, result.Subject);
            matched.Add((component, result));
        }

        var presentWeight = matched.Sum(pair => pair.Component.Weight);
        var coverage = presentWeight / Specification.TotalWeight;

        if (coverage < Specification.MinimumCoverage)
        {
            return CalculationOutcome.InsufficientData(
                Metric,
                InsufficientDataReason.MissingInput,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Only {coverage:0.##} of the score's weight is available and it requires " +
                    $"{Specification.MinimumCoverage:0.##}. Missing: " +
                    $"{string.Join(", ", MissingMetrics(matched))}."));
        }

        var terms = matched
            .Select(pair => CalculationInput.Create(
                pair.Component.Metric.Value,
                pair.Result.ToClaim(),
                pair.Result.Value.Unit))
            .ToList();

        var lookAhead = CalculationGuards.RefuseIfOutsideCutoff(Metric, context, terms);

        if (lookAhead is not null)
        {
            return lookAhead;
        }

        var weighted = matched.Sum(
            pair => pair.Component.Weight * pair.Component.Normalisation.Apply(pair.Result.Value.Amount));

        var score = weighted / presentWeight;

        // The latest state any component describes. A component describing an earlier period is
        // named in a caveat rather than quietly aged forward, because a score that silently mixes
        // periods is one nobody can date.
        var asOfUtc = matched.Max(pair => pair.Result.AsOfUtc);

        return CalculationOutcome.Computed(
            MetricResult.Create(
                context,
                Metric,
                MetricValue.Ratio(score),
                Formula(),
                CalculatorId,
                Version,
                asOfUtc,
                terms,
                Caveats(matched, coverage, asOfUtc)));
    }

    public override string ToString() => $"{Metric} ({Version}): {Specification.Description}";

    private string Formula() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"sum(weight x normalise(metric)) / sum(weight) over " +
            $"{Specification.Components.Count} declared components; see specification {Version}");

    private IEnumerable<string> MissingMetrics(
        List<(ScoreComponent Component, MetricResult Result)> matched)
    {
        var present = matched.Select(pair => pair.Component.Metric.Value).ToHashSet(StringComparer.Ordinal);

        return Specification.Components
            .Select(component => component.Metric.Value)
            .Where(metric => !present.Contains(metric));
    }

    private List<string> Caveats(
        List<(ScoreComponent Component, MetricResult Result)> matched,
        decimal coverage,
        DateTime asOfUtc)
    {
        var caveats = new List<string>();

        if (coverage < 1m)
        {
            caveats.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Computed from {coverage:0.##} of the score's declared weight. Absent: " +
                $"{string.Join(", ", MissingMetrics(matched))}."));
        }

        var stale = matched
            .Where(pair => pair.Result.AsOfUtc < asOfUtc)
            .Select(pair => string.Create(
                CultureInfo.InvariantCulture,
                $"{pair.Component.Metric} ({pair.Result.AsOfUtc:yyyy-MM-dd})"))
            .ToList();

        if (stale.Count > 0)
        {
            caveats.Add($"Components describing an earlier period than the score: {string.Join(", ", stale)}.");
        }

        return caveats;
    }
}
