using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Actions;

/// <summary>
/// The single entry point through which every side effect in the system passes.
/// </summary>
/// <remarks>
/// <para>
/// The shape of this method is the whole design. The effect is a delegate the caller supplies
/// but does NOT invoke: only the gateway invokes it, and only after the policy engine has
/// returned Execute. There is no way for a caller to obtain a decision and then choose to act
/// on it independently, because the acting and the deciding are the same call.
/// </para>
/// <para>
/// That is why this is a delegate rather than a "check, then do" pair of methods. A
/// <c>CanExecute()</c> followed by the caller's own write is a time-of-check-to-time-of-use gap
/// and, more importantly, is trivially forgettable.
/// </para>
/// </remarks>
public interface IActionGateway
{
    /// <summary>
    /// Evaluates policy for <paramref name="proposal"/> and, only if permitted, invokes
    /// <paramref name="effect"/>. Records the decision and the result in the audit trail
    /// whatever the outcome.
    /// </summary>
    Task<ActionOutcome<TResult>> DispatchAsync<TResult>(
        ActionProposal proposal,
        Func<CancellationToken, Task<TResult>> effect,
        CancellationToken cancellationToken = default);
}
