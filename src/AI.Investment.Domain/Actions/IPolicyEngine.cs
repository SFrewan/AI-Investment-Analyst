namespace AI.Investment.Domain.Actions;

/// <summary>
/// Decides whether a proposed action may execute, must be approved, or is refused.
/// </summary>
/// <remarks>
/// <para>
/// The contract deliberately has no <c>Task</c>, no <see cref="CancellationToken"/> and no
/// dependency on a clock or a store. Evaluation is a pure function of the proposal and the
/// context: no I/O, no ambient state, no possibility of a network failure changing the answer.
/// Anything that needs fetching is fetched into the <see cref="PolicyContext"/> beforehand,
/// by a caller that is responsible for failing closed if it cannot.
/// </para>
/// <para>
/// The interface exists so that the engine can be substituted in tests with a stub that returns
/// a fixed decision - proving that the gateway respects whatever the engine says. It is NOT an
/// extension point for alternative production implementations: there is one policy engine, and
/// making it swappable in production would make the safety guarantee configurable.
/// </para>
/// </remarks>
public interface IPolicyEngine
{
    /// <summary>
    /// Returns a decision. Total: never returns null, never throws for a well-formed proposal,
    /// and has no path that means "unknown".
    /// </summary>
    /// <param name="nowUtc">
    /// Supplied by the caller so the engine stays free of a clock dependency and its output is
    /// reproducible in tests.
    /// </param>
    PolicyDecision Evaluate(ActionProposal proposal, PolicyContext context, DateTime nowUtc);
}
