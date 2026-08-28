using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Ai;

/// <summary>A strongly-typed <see cref="AgentResult"/>.</summary>
/// <typeparam name="TOutput">The agent's structured output type.</typeparam>
/// <remarks>
/// Construction goes through <see cref="AgentResults"/>. The constructor is internal so that no
/// caller outside this assembly can assemble a result that bypasses the invariants in the base
/// type - which is the whole reason those invariants are there.
/// </remarks>
public sealed class AgentResult<TOutput> : AgentResult
    where TOutput : class
{
    internal AgentResult(
        AgentId agentId,
        string agentVersion,
        AgentStatus status,
        TOutput? output,
        Confidence? confidence,
        IEnumerable<ClaimId>? evidence,
        IEnumerable<string>? limitations,
        AgentDiagnostics diagnostics,
        string? explanation)
        : base(
            agentId,
            agentVersion,
            status,
            confidence,
            evidence,
            limitations,
            diagnostics,
            explanation,
            output is not null)
    {
        Output = output;
    }

    /// <summary>The structured answer. Null unless <see cref="AgentResult.Succeeded"/>.</summary>
    public TOutput? Output { get; }

    public override object? UntypedOutput => Output;

    /// <summary>
    /// Returns the output, or throws if the run did not succeed.
    /// </summary>
    /// <remarks>
    /// The explicit gate. Code that needs an answer calls this and fails loudly when there is
    /// none; code that can proceed without one reads <see cref="Output"/> and is thereby forced to
    /// handle the null at the call site rather than discovering it later as a zero.
    /// </remarks>
    public TOutput RequireOutput() =>
        Output ?? throw new DomainRuleViolationException(
            "AgentResult.OutputRequired",
            $"This code requires an analysis, but the {AgentId} run ended as {Status}: {Explanation}");

    /// <summary>
    /// Records the result in the epistemic model as an AI interpretation.
    /// </summary>
    /// <remarks>
    /// This is the only door between an agent and the claim graph, and it opens onto exactly one
    /// kind. An agent's output can never become a <c>Fact</c> or a <c>Calculation</c>, so nothing
    /// downstream that requires a measured value can consume it by accident - the deterministic
    /// calculators refuse a judgement outright, which is what makes the separation load-bearing
    /// rather than descriptive.
    /// </remarks>
    public Claim<TOutput> ToClaim(DateTime asOfUtc, DateTime producedAtUtc) =>
        Claims.AiInterpretation(
            RequireOutput(),
            Provenance.FromSystem(AgentId.ProducerId, asOfUtc, producedAtUtc),
            Evidence,
            Confidence!,
            Limitations);
}
