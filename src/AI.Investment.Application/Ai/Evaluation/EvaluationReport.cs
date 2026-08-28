using System.Globalization;
using System.Text;

namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>What an evaluation run measured, and whether it clears the bar.</summary>
/// <remarks>
/// <para>
/// Rates are computed rather than stored, and each denominator is chosen deliberately. A run that
/// never reached the parser - a provider outage, an exhausted budget - is excluded from both schema
/// validity and groundedness, because counting it would let an infrastructure problem look like a
/// fabrication problem, and the two need completely different responses.
/// </para>
/// <para>
/// The corollary is that an evaluation where nothing reached the model measures nothing, so
/// <see cref="Meets"/> refuses it outright rather than reporting a vacuous pass. A gate that goes
/// green because the run never happened is worse than one that goes red.
/// </para>
/// </remarks>
public sealed record EvaluationReport
{
    private readonly List<CaseOutcome> _cases;

    internal EvaluationReport(
        string agent,
        int repeats,
        int totalRuns,
        int providerFailures,
        int schemaFailures,
        int ungroundedRuns,
        List<CaseOutcome> cases)
    {
        Agent = agent;
        Repeats = repeats;
        TotalRuns = totalRuns;
        ProviderFailures = providerFailures;
        SchemaFailures = schemaFailures;
        UngroundedRuns = ungroundedRuns;
        _cases = cases;
    }

    public string Agent { get; }

    public int Repeats { get; }

    public int TotalRuns { get; }

    /// <summary>Runs that never reached the parser: the provider failed, or the budget was spent.</summary>
    public int ProviderFailures { get; }

    /// <summary>Runs whose answer could not be read into the agent's output type.</summary>
    public int SchemaFailures { get; }

    /// <summary>Runs that produced a readable answer quoting something not in the evidence.</summary>
    public int UngroundedRuns { get; }

    /// <summary>Runs where the provider answered at all, whatever the answer looked like.</summary>
    public int AnsweredRuns => TotalRuns - ProviderFailures;

    /// <summary>Runs that produced a readable answer or an explicit refusal.</summary>
    public int ParsedRuns => AnsweredRuns - SchemaFailures;

    public IReadOnlyList<CaseOutcome> Cases => _cases;

    public decimal SchemaValidity => Ratio(AnsweredRuns - SchemaFailures, AnsweredRuns);

    public decimal Groundedness => Ratio(ParsedRuns - UngroundedRuns, ParsedRuns);

    public decimal Stability => Ratio(_cases.Count(outcome => outcome.Stable), _cases.Count);

    public decimal ExpectationAccuracy => Ratio(_cases.Count(outcome => outcome.MetExpectation), _cases.Count);

    /// <summary>True when something was actually measured and every rate is at or above its threshold.</summary>
    public bool Meets(EvaluationThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        return ParsedRuns > 0 &&
               SchemaValidity >= thresholds.MinSchemaValidity &&
               Groundedness >= thresholds.MinGroundedness &&
               Stability >= thresholds.MinStability &&
               ExpectationAccuracy >= thresholds.MinExpectationAccuracy;
    }

    /// <summary>A report a human can read, naming every case that did not behave.</summary>
    public string Explain()
    {
        var builder = new StringBuilder();

        builder.Append(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Agent}: {_cases.Count} cases x {Repeats} repeats = {TotalRuns} runs " +
                $"({ProviderFailures} never reached the provider). " +
                $"schema={SchemaValidity:0.###} grounded={Groundedness:0.###} " +
                $"stable={Stability:0.###} expected={ExpectationAccuracy:0.###}"));

        if (ParsedRuns == 0)
        {
            builder.Append('\n').Append("  nothing was measured: no run produced a readable answer");
        }

        foreach (var outcome in _cases)
        {
            if (!outcome.MetExpectation || !outcome.Stable)
            {
                builder.Append('\n').Append("  ").Append(outcome);
            }
        }

        return builder.ToString();
    }

    public override string ToString() => Explain();

    private static decimal Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0m : (decimal)numerator / denominator;
}
