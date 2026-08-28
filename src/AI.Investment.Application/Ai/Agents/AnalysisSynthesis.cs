using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>The synthesis agent's account of what the specialists collectively found.</summary>
public sealed class AnalysisSynthesis : IGroundedOutput
{
    private readonly List<string> _keyPoints;
    private readonly List<AssertedFigure> _figures;

    public AnalysisSynthesis(
        string narrative,
        AnalysisStance stance,
        IEnumerable<string> keyPoints,
        IEnumerable<AssertedFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(keyPoints);
        ArgumentNullException.ThrowIfNull(figures);

        Narrative = narrative;
        Stance = stance;
        _keyPoints = keyPoints.ToList();
        _figures = figures.ToList();
    }

    public string Narrative { get; }

    public AnalysisStance Stance { get; }

    public IReadOnlyList<string> KeyPoints => _keyPoints;

    public IReadOnlyList<AssertedFigure> Figures => _figures;

    public IReadOnlyList<AssertedFigure> AssertedFigures() => _figures;

    public IReadOnlyList<string> NarrativeFragments()
    {
        var fragments = new List<string>(1 + _keyPoints.Count) { Narrative };

        fragments.AddRange(_keyPoints);

        return fragments;
    }
}
