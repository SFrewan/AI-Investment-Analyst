using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// What could go wrong with this opportunity, and how hard it would be to undo.
/// </summary>
/// <remarks>
/// <para>
/// Mandatory: an opportunity cannot leave <see cref="OpportunityStatus.Draft"/> without one. A
/// candidate with no stated downside reads as a candidate with no downside, and the absence is
/// invisible next to a filled-in economics block.
/// </para>
/// <para>
/// <see cref="Reversibility"/> is here rather than on the economics because it is the primary axis
/// of risk in this system and it drives the policy decision: a small irreversible commitment
/// deserves more scrutiny than a large reversible one. It is stated by the type's own requirements,
/// never inferred from the amount.
/// </para>
/// <para>
/// Named <c>OpportunityRisk</c> rather than <c>RiskAssessment</c> as the architecture sketch has it,
/// because <c>RiskAssessment</c> is already the risk agent's output type in the application layer.
/// Two different things with one name, one of them a model's opinion and the other a mandatory field
/// of a domain aggregate, is a confusion worth a longer identifier to avoid.
/// </para>
/// </remarks>
public sealed record OpportunityRisk
{
    public const int MaxSummaryLength = 1000;

    private readonly List<string> _factors;
    private readonly List<ClaimId> _evidence;

    private OpportunityRisk(
        string summary,
        ReversibilityClass reversibility,
        List<string> factors,
        List<ClaimId> evidence)
    {
        Summary = summary;
        Reversibility = reversibility;
        _factors = factors;
        _evidence = evidence;
    }

    public string Summary { get; }

    /// <summary>How hard the resulting action would be to undo. Drives the policy decision.</summary>
    public ReversibilityClass Reversibility { get; }

    /// <summary>The individual things identified as able to go wrong.</summary>
    public IReadOnlyList<string> Factors => _factors;

    /// <summary>The claims the assessment rests on. At least one is required.</summary>
    public IReadOnlyList<ClaimId> Evidence => _evidence;

    public static OpportunityRisk Create(
        string summary,
        ReversibilityClass reversibility,
        IEnumerable<ClaimId> evidence,
        IEnumerable<string>? factors = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainValidationException(
                nameof(summary),
                "A risk assessment must say something. An empty summary beside a filled-in economics " +
                "block reads as an opportunity with no downside.");
        }

        if (!Enum.IsDefined(reversibility))
        {
            throw new DomainValidationException(
                nameof(reversibility),
                $"Unrecognised reversibility class '{reversibility}'.");
        }

        var claims = evidence.Distinct().ToList();

        if (claims.Count == 0)
        {
            throw new DomainRuleViolationException(
                "OpportunityRisk.CitesEvidence",
                "A risk assessment must cite at least one claim. An assessment resting on nothing " +
                "cannot be checked, and it is indistinguishable from one that was never made.");
        }

        var trimmed = summary.Trim();

        return new OpportunityRisk(
            trimmed.Length <= MaxSummaryLength ? trimmed : trimmed[..MaxSummaryLength],
            reversibility,
            factors?
                .Where(factor => !string.IsNullOrWhiteSpace(factor))
                .Select(factor => factor.Trim())
                .ToList() ?? [],
            claims);
    }

    public override string ToString() => $"[{Reversibility}] {Summary}";
}
