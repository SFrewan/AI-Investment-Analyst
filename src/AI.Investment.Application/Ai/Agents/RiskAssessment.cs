using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>The risk agent's enumeration of what could go wrong, from the evidence it was given.</summary>
/// <remarks>
/// Identification only. Nothing here is a limit, a ceiling or an authorisation input: the risk that
/// governs whether an action may run is computed by <c>RiskTierCalculator</c> from economics and
/// reversibility, and no model contributes to it.
/// </remarks>
public sealed class RiskAssessment : IGroundedOutput
{
    private readonly List<IdentifiedRisk> _risks;
    private readonly List<AssertedFigure> _figures;

    public RiskAssessment(
        string summary,
        IEnumerable<IdentifiedRisk> risks,
        IEnumerable<AssertedFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(risks);
        ArgumentNullException.ThrowIfNull(figures);

        Summary = summary;
        _risks = risks.ToList();
        _figures = figures.ToList();
    }

    public string Summary { get; }

    public IReadOnlyList<IdentifiedRisk> Risks => _risks;

    public IReadOnlyList<AssertedFigure> Figures => _figures;

    /// <summary>The most serious severity identified, or <see cref="RiskSeverity.Unknown"/> if none.</summary>
    public RiskSeverity HighestSeverity =>
        _risks.Count == 0 ? RiskSeverity.Unknown : _risks.Max(risk => risk.Severity);

    public IReadOnlyList<AssertedFigure> AssertedFigures() => _figures;

    public IReadOnlyList<string> NarrativeFragments()
    {
        var fragments = new List<string>(1 + _risks.Count) { Summary };

        fragments.AddRange(_risks.Select(risk => risk.Description));

        return fragments;
    }
}
