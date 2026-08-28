using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>What the harness observed for one case, across all its repeats.</summary>
public sealed record CaseOutcome
{
    private readonly List<AgentStatus> _statuses;

    internal CaseOutcome(
        EvaluationCase evaluationCase,
        List<AgentStatus> statuses,
        bool stable,
        bool metExpectation,
        string? note)
    {
        Case = evaluationCase;
        _statuses = statuses;
        Stable = stable;
        MetExpectation = metExpectation;
        Note = note;
    }

    public EvaluationCase Case { get; }

    /// <summary>How each repeat ended, in order.</summary>
    public IReadOnlyList<AgentStatus> Statuses => _statuses;

    /// <summary>True when every repeat produced an identical result.</summary>
    public bool Stable { get; }

    /// <summary>True when the observed behaviour matched what the case said it expected.</summary>
    public bool MetExpectation { get; }

    /// <summary>Why the case failed, when it did.</summary>
    public string? Note { get; }

    public override string ToString() =>
        MetExpectation && Stable
            ? $"{Case.Name}: ok ({_statuses[0]})"
            : $"{Case.Name}: {(MetExpectation ? "unstable" : "unexpected")} - {Note}";
}
