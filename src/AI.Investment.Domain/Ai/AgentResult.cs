using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// The outcome of one agent run: a typed answer, or a stated refusal - never free text.
/// </summary>
/// <remarks>
/// <para>
/// The non-generic base exists so that results of differing output types can be held in one
/// collection, audited through one path and inspected without knowing the type argument. The
/// typed output lives on <see cref="AgentResult{T}"/>, exactly as <c>Claim</c> and
/// <c>Claim&lt;T&gt;</c> are split.
/// </para>
/// <para>
/// <strong>Limitations and a refusal status are not decoration.</strong> An agent with no way to
/// say "I could not determine this" will fill the gap, and an invented margin figure is worse than
/// a missing one precisely because it is indistinguishable from a real one everywhere downstream.
/// The invariants below are what force that admission to be structural: a successful result must
/// carry a confidence and cite evidence, and an unsuccessful one may not carry an output at all.
/// </para>
/// </remarks>
public abstract class AgentResult
{
    private readonly List<ClaimId> _evidence;
    private readonly List<string> _limitations;

    private protected AgentResult(
        AgentId agentId,
        string agentVersion,
        AgentStatus status,
        Confidence? confidence,
        IEnumerable<ClaimId>? evidence,
        IEnumerable<string>? limitations,
        AgentDiagnostics diagnostics,
        string? explanation,
        bool hasOutput)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _evidence = evidence?.Distinct().ToList() ?? [];
        _limitations = limitations?
            .Where(limitation => !string.IsNullOrWhiteSpace(limitation))
            .Select(limitation => limitation.Trim())
            .ToList() ?? [];

        Validate(status, confidence, _evidence, explanation, hasOutput);

        AgentId = agentId;
        AgentVersion = RequireVersion(agentVersion);
        Status = status;
        Confidence = confidence;
        Diagnostics = diagnostics;
        Explanation = explanation?.Trim();
    }

    public AgentId AgentId { get; }

    /// <summary>The agent implementation's own version, recorded alongside the prompt version.</summary>
    public string AgentVersion { get; }

    public AgentStatus Status { get; }

    /// <summary>Present only when <see cref="Succeeded"/>.</summary>
    public Confidence? Confidence { get; }

    /// <summary>The claims the agent actually used. Never empty for a successful result.</summary>
    public IReadOnlyList<ClaimId> Evidence => _evidence;

    /// <summary>What the agent could NOT determine. Empty is permitted; silence is not the same thing.</summary>
    public IReadOnlyList<string> Limitations => _limitations;

    public AgentDiagnostics Diagnostics { get; }

    /// <summary>Why the run did not succeed. Always present when it did not.</summary>
    public string? Explanation { get; }

    public bool Succeeded => Status == AgentStatus.Ok;

    /// <summary>The output, untyped. Prefer <see cref="AgentResult{T}.Output"/>.</summary>
    public abstract object? UntypedOutput { get; }

    public override string ToString() =>
        Succeeded
            ? $"{AgentId} {Status} ({Confidence})"
            : $"{AgentId} {Status}: {Explanation}";

    private static string RequireVersion(string agentVersion)
    {
        if (string.IsNullOrWhiteSpace(agentVersion))
        {
            throw new DomainValidationException(
                nameof(agentVersion),
                "An agent must state its own version. Without it, a change in behaviour cannot be " +
                "separated from a change in the prompt or in the model.");
        }

        return agentVersion.Trim();
    }

    /// <summary>
    /// The rules that stop a result from being simultaneously a failure and an answer.
    /// </summary>
    /// <remarks>
    /// <paramref name="evidence"/> and <paramref name="limitations"/> are the concrete
    /// <see cref="List{T}"/>s rather than interfaces (CA1859): this is a private helper with one
    /// call site that always passes the backing fields.
    /// </remarks>
    private static void Validate(
        AgentStatus status,
        Confidence? confidence,
        List<ClaimId> evidence,
        string? explanation,
        bool hasOutput)
    {
        if (status == AgentStatus.Unknown)
        {
            throw new DomainRuleViolationException(
                "AgentResult.StatusRequired",
                "A completed run must state how it ended. An unset status would present a run that " +
                "never happened as a successful analysis.");
        }

        if (status == AgentStatus.Ok)
        {
            if (!hasOutput)
            {
                throw new DomainRuleViolationException(
                    "AgentResult.SuccessCarriesOutput",
                    "A successful agent run must carry its output.");
            }

            if (confidence is null)
            {
                throw new DomainRuleViolationException(
                    "AgentResult.JudgementStatesConfidence",
                    "An agent result is a judgement and must state its confidence. A judgement " +
                    "presented without stated uncertainty is indistinguishable downstream from a " +
                    "measured fact.");
            }

            if (evidence.Count == 0)
            {
                throw new DomainRuleViolationException(
                    "AgentResult.JudgementCitesEvidence",
                    "An agent result must cite the claims it used. A judgement with no traceable " +
                    "supporting claim cannot be checked for groundedness and must be treated as " +
                    "fabricated.");
            }

            return;
        }

        if (hasOutput)
        {
            throw new DomainRuleViolationException(
                "AgentResult.FailureCarriesNoOutput",
                $"A run that ended as {status} may not also carry an output. A partially trusted " +
                "answer is one that will be read as a whole one.");
        }

        if (confidence is not null)
        {
            throw new DomainRuleViolationException(
                "AgentResult.FailureStatesNoConfidence",
                $"A run that ended as {status} produced nothing to be confident about.");
        }

        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new DomainRuleViolationException(
                "AgentResult.FailureIsExplained",
                $"A run that ended as {status} must say why. An unexplained refusal is impossible to " +
                "act on and impossible to distinguish from a defect.");
        }
    }
}
