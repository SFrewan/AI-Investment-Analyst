using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>The financial agent's reading of a company's reported figures.</summary>
/// <remarks>
/// Interpretation, not arithmetic. Every ratio in this system is computed deterministically by the
/// Phase 3 calculators and arrives in the bundle already; what an agent adds is the reading - which
/// of those numbers matter together, and what they suggest. The separation is why
/// <see cref="Figures"/> exists: an agent may quote a computed figure, and may not compute one.
/// </remarks>
public sealed class FinancialReading : IGroundedOutput
{
    private readonly List<AssertedFigure> _figures;
    private readonly List<string> _strengths;
    private readonly List<string> _concerns;

    public FinancialReading(
        string summary,
        IEnumerable<AssertedFigure> figures,
        IEnumerable<string> strengths,
        IEnumerable<string> concerns)
    {
        ArgumentNullException.ThrowIfNull(figures);
        ArgumentNullException.ThrowIfNull(strengths);
        ArgumentNullException.ThrowIfNull(concerns);

        Summary = summary;
        _figures = figures.ToList();
        _strengths = strengths.ToList();
        _concerns = concerns.ToList();
    }

    public string Summary { get; }

    public IReadOnlyList<AssertedFigure> Figures => _figures;

    public IReadOnlyList<string> Strengths => _strengths;

    public IReadOnlyList<string> Concerns => _concerns;

    public IReadOnlyList<AssertedFigure> AssertedFigures() => _figures;

    public IReadOnlyList<string> NarrativeFragments()
    {
        var fragments = new List<string>(1 + _strengths.Count + _concerns.Count) { Summary };

        fragments.AddRange(_strengths);
        fragments.AddRange(_concerns);

        return fragments;
    }
}
