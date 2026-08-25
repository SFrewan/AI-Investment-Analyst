using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Ambient flag saying whether an authorised action execution is currently in progress.
/// </summary>
/// <remarks>
/// <para>
/// This is the second, independent half of the guarantee that a write cannot bypass the safety
/// seam. The first half is in the domain: <c>ActionExecution.Start</c> refuses a decision that
/// does not authorise its proposal. The second is here: the persistence layer consults this
/// before committing anything, and throws <see cref="UnauthorizedWriteException"/> if no
/// authorised execution is open.
/// </para>
/// <para>
/// Two mechanisms rather than one, because a single mechanism can be forgotten at a call site.
/// A developer who writes a repository call and a <c>SaveChangesAsync</c> outside the gateway
/// does not get a silent write - they get an exception naming the rule they broke.
/// </para>
/// <para>
/// Only <c>ActionGateway</c> calls <see cref="Authorize"/>, and only after the policy engine has
/// returned Execute.
/// </para>
/// </remarks>
public interface IWriteAuthorization
{
    /// <summary>True while an authorised execution is in progress on this scope.</summary>
    bool IsAuthorized { get; }

    /// <summary>The decision currently authorising writes, if any.</summary>
    Guid? AuthorizingDecisionId { get; }

    /// <summary>
    /// Opens an authorisation window. Throws unless the decision permits execution. Disposing
    /// the returned handle closes the window.
    /// </summary>
    IDisposable Authorize(PolicyDecision decision);
}
