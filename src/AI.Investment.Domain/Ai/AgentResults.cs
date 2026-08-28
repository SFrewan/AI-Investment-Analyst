using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// The only way to create an <see cref="AgentResult{TOutput}"/>. One factory per outcome.
/// </summary>
/// <remarks>
/// A non-generic host class so the type argument is inferred at the call site, and so that no
/// static member hangs off a generic type. The same shape as <c>Claims</c>, for the same reasons.
/// </remarks>
public static class AgentResults
{
    /// <summary>A successful run: a typed answer, a stated confidence, and the evidence used.</summary>
    public static AgentResult<TOutput> Ok<TOutput>(
        AgentId agentId,
        string agentVersion,
        TOutput output,
        Confidence confidence,
        IEnumerable<ClaimId> evidence,
        AgentDiagnostics diagnostics,
        IEnumerable<string>? limitations = null)
        where TOutput : class
    {
        ArgumentNullException.ThrowIfNull(output);

        return new AgentResult<TOutput>(
            agentId,
            agentVersion,
            AgentStatus.Ok,
            output,
            confidence,
            evidence,
            limitations,
            diagnostics,
            explanation: null);
    }

    /// <summary>
    /// A run that produced nothing usable. The reason is required, and the output is structurally
    /// absent rather than empty.
    /// </summary>
    public static AgentResult<TOutput> Failed<TOutput>(
        AgentId agentId,
        string agentVersion,
        AgentStatus status,
        string explanation,
        AgentDiagnostics diagnostics,
        IEnumerable<ClaimId>? evidence = null,
        IEnumerable<string>? limitations = null)
        where TOutput : class =>
        new(
            agentId,
            agentVersion,
            status,
            output: null,
            confidence: null,
            evidence,
            limitations,
            diagnostics,
            explanation);
}
