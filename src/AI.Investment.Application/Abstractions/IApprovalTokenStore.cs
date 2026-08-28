using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Opportunities;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores approval tokens and consumes them exactly once.</summary>
/// <remarks>
/// <para>
/// <see cref="ConsumeAsync"/> is separate from an ordinary save because single use has to be
/// enforced by the database, not only by the aggregate. The in-memory check cannot see a concurrent
/// caller, and "it retried and bought twice" is the most likely way this platform loses money first.
/// An implementation must consume conditionally on the token still being unconsumed and report
/// failure when no row was affected.
/// </para>
/// <para>
/// There is no method to un-consume one.
/// </para>
/// </remarks>
public interface IApprovalTokenStore
{
    Task AddAsync(ApprovalToken token, CancellationToken cancellationToken = default);

    Task<ApprovalToken?> GetAsync(Guid approvalTokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes the token for exactly this action, atomically.
    /// </summary>
    /// <returns>
    /// <see cref="ApprovalRefusal.None"/> when the token was consumed by this call, and the reason
    /// otherwise - including when another caller consumed it first.
    /// </returns>
    Task<ApprovalRefusal> ConsumeAsync(
        Guid approvalTokenId,
        OpportunityId opportunityId,
        ActionProposal proposal,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
