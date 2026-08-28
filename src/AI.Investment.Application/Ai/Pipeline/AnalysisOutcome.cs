using System.Globalization;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Pipeline;

/// <summary>Everything one analysis run produced, including what it failed to produce.</summary>
/// <remarks>
/// Failed specialist runs are kept, not discarded. A run where the news agent refused and the other
/// two succeeded is a different thing from a run where all three succeeded, and an outcome that
/// only carried the successes would make the two indistinguishable to anything reading it later.
/// </remarks>
public sealed record AnalysisOutcome
{
    private readonly List<AgentResult> _specialists;

    internal AnalysisOutcome(
        EvidenceBundle bundle,
        List<AgentResult> specialists,
        AgentResult<AnalysisSynthesis>? synthesis,
        AnalysisBudget budget)
    {
        Bundle = bundle;
        _specialists = specialists;
        Synthesis = synthesis;
        Budget = budget;
    }

    public EvidenceBundle Bundle { get; }

    /// <summary>Every specialist run, successful or not.</summary>
    public IReadOnlyList<AgentResult> Specialists => _specialists;

    /// <summary>
    /// The synthesis, when there was anything to synthesise. Null when every specialist failed.
    /// </summary>
    public AgentResult<AnalysisSynthesis>? Synthesis { get; }

    public AnalysisBudget Budget { get; }

    public int SucceededCount => _specialists.Count(result => result.Succeeded);

    /// <summary>True when at least one specialist succeeded and the synthesis did too.</summary>
    public bool IsComplete => Synthesis is not null && Synthesis.Succeeded;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Bundle.Subject}: {SucceededCount}/{_specialists.Count} specialists, " +
            $"synthesis={(Synthesis is null ? "none" : Synthesis.Status.ToString())}, {Budget}");
}
