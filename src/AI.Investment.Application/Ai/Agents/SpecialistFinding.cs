using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>
/// One specialist's validated output, reduced to what the synthesis agent is allowed to see.
/// </summary>
/// <remarks>
/// A deliberate narrowing. Synthesis reads findings that have already passed schema validation and
/// the groundedness check - never a raw answer, and never one that failed - so a fabricated figure
/// cannot enter the final narrative by way of a specialist that was excluded from scoring. The
/// reduction also keeps the synthesis prompt small, which matters because it is the one call that
/// reads everything.
/// </remarks>
public sealed record SpecialistFinding
{
    private readonly List<string> _points;
    private readonly List<AssertedFigure> _figures;

    private SpecialistFinding(
        AgentId agent,
        Confidence confidence,
        string summary,
        List<string> points,
        List<AssertedFigure> figures)
    {
        Agent = agent;
        Confidence = confidence;
        Summary = summary;
        _points = points;
        _figures = figures;
    }

    public AgentId Agent { get; }

    public Confidence Confidence { get; }

    public string Summary { get; }

    public IReadOnlyList<string> Points => _points;

    /// <summary>The figures this specialist stated, every one of them already matched to a claim.</summary>
    public IReadOnlyList<AssertedFigure> Figures => _figures;

    public static SpecialistFinding Create(
        AgentId agent,
        Confidence confidence,
        string summary,
        IEnumerable<string> points,
        IEnumerable<AssertedFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(confidence);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(figures);

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainValidationException(nameof(summary), "A specialist finding must carry its summary.");
        }

        return new SpecialistFinding(
            agent,
            confidence,
            summary.Trim(),
            points.Where(point => !string.IsNullOrWhiteSpace(point)).Select(point => point.Trim()).ToList(),
            figures.ToList());
    }

    public override string ToString() => $"{Agent} ({Confidence}): {Summary}";
}
