using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Approvals;

/// <summary>
/// A human's permission to perform one exact action, once, before a stated time.
/// </summary>
/// <remarks>
/// <para>
/// Four properties, and each exists because its absence is a known way approvals stop meaning
/// anything:
/// </para>
/// <list type="bullet">
/// <item><strong>Single-use.</strong> A token that can be replayed authorises an unbounded number of
/// actions, and "it retried and bought twice" is the most likely way this platform loses money
/// first.</item>
/// <item><strong>Expiring.</strong> A standing approval is indistinguishable from no approval by the
/// time it is used, because the conditions it was granted under have gone.</item>
/// <item><strong>Bound to a fingerprint.</strong> It approves the action that was on the screen, not
/// a larger or different one assembled afterwards.</item>
/// <item><strong>Capped.</strong> Even for the same action shape, it cannot authorise more than the
/// amount the approver saw.</item>
/// </list>
/// <para>
/// There is no status field. Whether a token is usable is derived from what has happened to it -
/// consumed, revoked, or past its expiry - so there is no defaultable enum that a deserialisation
/// or a missed assignment could leave in a permissive state.
/// </para>
/// <para>
/// Single use is enforced here and again in the store, which consumes conditionally on the token
/// still being unconsumed. Two mechanisms, because the in-memory one cannot see a concurrent caller.
/// </para>
/// </remarks>
public sealed class ApprovalToken
{
    public const int MaxApproverLength = 120;
    public const int MaxReasonLength = 500;

    private ApprovalToken(
        Guid approvalTokenId,
        OpportunityId opportunityId,
        Guid proposalId,
        ActionFingerprint fingerprint,
        Money maxAmount,
        string approvedBy,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        ApprovalTokenId = approvalTokenId;
        OpportunityId = opportunityId;
        ProposalId = proposalId;
        Fingerprint = fingerprint;
        MaxAmount = maxAmount;
        ApprovedBy = approvedBy;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private ApprovalToken()
    {
        Fingerprint = null!;
        MaxAmount = null!;
        ApprovedBy = string.Empty;
    }

    public Guid ApprovalTokenId { get; private set; }

    public OpportunityId OpportunityId { get; private set; }

    public Guid ProposalId { get; private set; }

    /// <summary>The hash of the exact action the approver was shown.</summary>
    public ActionFingerprint Fingerprint { get; private set; }

    /// <summary>The most this token may commit. Never more than the approver saw.</summary>
    public Money MaxAmount { get; private set; }

    /// <summary>Who approved it. A person, never a service and never an agent.</summary>
    public string ApprovedBy { get; private set; }

    public DateTime IssuedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? ConsumedAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public string? RevocationReason { get; private set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool HasExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>
    /// Issues a token for exactly the action described by <paramref name="proposal"/>.
    /// </summary>
    /// <remarks>
    /// The fingerprint is taken from the proposal here rather than accepted as a parameter, so that
    /// a token cannot be issued for a hash of something other than the action it names.
    /// </remarks>
    public static ApprovalToken Issue(
        OpportunityId opportunityId,
        ActionProposal proposal,
        Money maxAmount,
        string approvedBy,
        DateTime nowUtc,
        TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(maxAmount);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new DomainValidationException(
                nameof(approvedBy),
                "An approval must name the person who gave it. An unattributed approval cannot be " +
                "questioned afterwards, which is most of what an approval is for.");
        }

        var approver = approvedBy.Trim();

        if (approver.Length > MaxApproverLength)
        {
            throw new DomainValidationException(
                nameof(approvedBy),
                $"An approver identifier may not exceed {MaxApproverLength} characters.");
        }

        if (maxAmount.IsNegative)
        {
            throw new DomainValidationException(nameof(maxAmount), "An approval ceiling may not be negative.");
        }

        if (validFor <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(validFor),
                "An approval must expire. A token with no window is a standing permission, which is " +
                "indistinguishable from no approval by the time it is used.");
        }

        if (maxAmount.Currency != proposal.Economics.EstimatedExposure.Currency)
        {
            throw new DomainValidationException(
                nameof(maxAmount),
                $"The approval ceiling is in {maxAmount.Currency} but the action is in " +
                $"{proposal.Economics.EstimatedExposure.Currency}. A ceiling that cannot be compared " +
                "would never bind.");
        }

        return new ApprovalToken(
            Guid.NewGuid(),
            opportunityId,
            proposal.ProposalId,
            ActionFingerprint.Of(proposal),
            maxAmount,
            approver,
            nowUtc,
            nowUtc.Add(validFor));
    }

    /// <summary>
    /// Checks this token against the action about to be performed, without consuming it.
    /// </summary>
    /// <returns><see cref="ApprovalRefusal.None"/> when the token authorises exactly this action.</returns>
    public ApprovalRefusal Check(OpportunityId opportunityId, ActionProposal proposal, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (IsConsumed)
        {
            return ApprovalRefusal.AlreadyConsumed;
        }

        if (IsRevoked)
        {
            return ApprovalRefusal.Revoked;
        }

        if (HasExpired(nowUtc))
        {
            return ApprovalRefusal.Expired;
        }

        if (!OpportunityId.Equals(opportunityId))
        {
            return ApprovalRefusal.WrongOpportunity;
        }

        if (ProposalId != proposal.ProposalId)
        {
            return ApprovalRefusal.WrongProposal;
        }

        if (!Fingerprint.Matches(proposal))
        {
            return ApprovalRefusal.FingerprintMismatch;
        }

        var exposure = proposal.Economics.EstimatedExposure;

        if (exposure.Currency != MaxAmount.Currency || exposure.IsGreaterThan(MaxAmount))
        {
            return ApprovalRefusal.AmountExceeded;
        }

        return ApprovalRefusal.None;
    }

    /// <summary>
    /// Consumes the token for this action, or throws with the reason it could not be used.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning false is deliberate: consuming an approval is the last gate
    /// before an effect, and a caller that ignores a returned boolean at that point has just
    /// performed an unapproved action.
    /// </remarks>
    public void Consume(OpportunityId opportunityId, ActionProposal proposal, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var refusal = Check(opportunityId, proposal, nowUtc);

        if (refusal != ApprovalRefusal.None)
        {
            throw new DomainRuleViolationException(
                "ApprovalToken." + refusal,
                $"Approval {ApprovalTokenId} cannot authorise this action: {Describe(refusal)}");
        }

        ConsumedAtUtc = nowUtc;
    }

    /// <summary>Withdraws the token before it is used.</summary>
    public void Revoke(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (IsConsumed)
        {
            throw new DomainRuleViolationException(
                "ApprovalToken.AlreadyConsumed",
                "A consumed approval cannot be revoked. The action it authorised has already happened, " +
                "and pretending otherwise would make the record wrong.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(nameof(reason), "A revocation must state a reason.");
        }

        var trimmed = reason.Trim();

        RevokedAtUtc = nowUtc;
        RevocationReason = trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength];
    }

    public override string ToString() =>
        $"approval {ApprovalTokenId} for {OpportunityId} by {ApprovedBy}, expires {ExpiresAtUtc:O}";

    private static string Describe(ApprovalRefusal refusal) =>
        refusal switch
        {
            ApprovalRefusal.AlreadyConsumed => "it has already been used, and approvals are single-use.",
            ApprovalRefusal.Revoked => "it was revoked before it could be used.",
            ApprovalRefusal.Expired => "its window has passed.",
            ApprovalRefusal.FingerprintMismatch =>
                "the action presented for execution is not the action that was approved.",
            ApprovalRefusal.AmountExceeded => "the action would commit more than the approver saw.",
            ApprovalRefusal.WrongOpportunity => "it belongs to a different opportunity.",
            ApprovalRefusal.WrongProposal => "it belongs to a different proposal.",
            _ => "no reason was recorded, which is itself a defect.",
        };
}
