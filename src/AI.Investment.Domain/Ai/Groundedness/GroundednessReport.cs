using System.Globalization;
using AI.Investment.Domain.Evidence;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// The verdict on one agent output: which figures trace to evidence, and which do not.
/// </summary>
/// <remarks>
/// <see cref="MatchedClaims"/> is the list of claims the output <em>demonstrably</em> used, derived
/// by the validator rather than reported by the agent. That distinction matters: an agent's own
/// account of what it read is exactly the sort of thing a model gets wrong or embellishes, and an
/// evidence list nobody checked is decoration. What the validator matched is what the result is
/// allowed to cite.
/// </remarks>
public sealed record GroundednessReport
{
    private readonly List<FigureFinding> _figures;
    private readonly List<NumericMention> _ungroundedMentions;
    private readonly List<ClaimId> _matchedClaims;

    internal GroundednessReport(
        GroundednessPolicy policy,
        List<FigureFinding> figures,
        List<NumericMention> ungroundedMentions,
        List<ClaimId> matchedClaims)
    {
        Policy = policy;
        _figures = figures;
        _ungroundedMentions = ungroundedMentions;
        _matchedClaims = matchedClaims;
    }

    public GroundednessPolicy Policy { get; }

    public IReadOnlyList<FigureFinding> Figures => _figures;

    /// <summary>Numerals found in prose that trace to nothing in the bundle.</summary>
    public IReadOnlyList<NumericMention> UngroundedMentions => _ungroundedMentions;

    /// <summary>The claims the output was actually shown to rest on.</summary>
    public IReadOnlyList<ClaimId> MatchedClaims => _matchedClaims;

    public bool IsGrounded =>
        !_figures.Exists(finding => !finding.IsGrounded) &&
        (Policy == GroundednessPolicy.Structural || _ungroundedMentions.Count == 0);

    /// <summary>A one-line account of why the output failed, suitable for an audit record.</summary>
    public string Explain()
    {
        if (IsGrounded)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Grounded: {_figures.Count} figures traced to {_matchedClaims.Count} claims.");
        }

        var failures = new List<string>();

        foreach (var finding in _figures)
        {
            if (!finding.IsGrounded)
            {
                failures.Add(finding.ToString());
            }
        }

        if (_ungroundedMentions.Count > 0 && Policy != GroundednessPolicy.Structural)
        {
            failures.Add(
                "figures in prose with no supporting claim: " +
                string.Join(", ", _ungroundedMentions.Select(mention => mention.Text)));
        }

        return "Ungrounded. " + string.Join("; ", failures);
    }

    public override string ToString() => Explain();
}
