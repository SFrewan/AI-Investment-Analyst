using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Approvals;

/// <summary>
/// Exactly what a human is shown when asked to approve an action.
/// </summary>
/// <remarks>
/// <para>
/// A read model rather than an entity, assembled from the opportunity and the proposal. It carries
/// the fingerprint of the action it describes, so the token issued from it is bound to the thing on
/// the screen rather than to whatever is assembled later.
/// </para>
/// <para>
/// <strong>Uncertainty is a required field</strong>, and there is no constructor without it. §L.9
/// puts this in the type system precisely because a disclaimer in a template can be removed by
/// someone tidying the layout, and nobody notices until a confident-looking recommendation turns
/// out to have been a guess.
/// </para>
/// </remarks>
public sealed record ApprovalRequest
{
    private readonly List<string> _riskFactors;

    private ApprovalRequest(
        OpportunityId opportunityId,
        Guid proposalId,
        ActionFingerprint fingerprint,
        string title,
        Capability capability,
        ActionType actionType,
        ActionTarget target,
        Money estimatedCost,
        Money estimatedExposure,
        ReversibilityClass reversibility,
        RiskTier riskTier,
        Confidence confidence,
        string riskSummary,
        List<string> riskFactors,
        int evidenceCount,
        DateTime requestedAtUtc)
    {
        OpportunityId = opportunityId;
        ProposalId = proposalId;
        Fingerprint = fingerprint;
        Title = title;
        Capability = capability;
        ActionType = actionType;
        Target = target;
        EstimatedCost = estimatedCost;
        EstimatedExposure = estimatedExposure;
        Reversibility = reversibility;
        RiskTier = riskTier;
        Confidence = confidence;
        RiskSummary = riskSummary;
        _riskFactors = riskFactors;
        EvidenceCount = evidenceCount;
        RequestedAtUtc = requestedAtUtc;
    }

    public OpportunityId OpportunityId { get; }

    public Guid ProposalId { get; }

    /// <summary>The hash of the action described here. A token is issued against this.</summary>
    public ActionFingerprint Fingerprint { get; }

    public string Title { get; }

    public Capability Capability { get; }

    public ActionType ActionType { get; }

    public ActionTarget Target { get; }

    public Money EstimatedCost { get; }

    public Money EstimatedExposure { get; }

    public ReversibilityClass Reversibility { get; }

    /// <summary>Computed, never asserted by whoever proposed the action.</summary>
    public RiskTier RiskTier { get; }

    /// <summary>The mandatory uncertainty. There is no request without it.</summary>
    public Confidence Confidence { get; }

    public string RiskSummary { get; }

    public IReadOnlyList<string> RiskFactors => _riskFactors;

    public int EvidenceCount { get; }

    public DateTime RequestedAtUtc { get; }

    public static ApprovalRequest For(Opportunity opportunity, ActionProposal proposal, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(proposal);

        if (opportunity.Risk is not { } risk)
        {
            throw new DomainRuleViolationException(
                "ApprovalRequest.RequiresRisk",
                "An approval request cannot be built for an opportunity with no risk assessment. " +
                "Asking a person to approve something whose downside was never stated is asking them " +
                "to rubber-stamp it.");
        }

        if (opportunity.Confidence is not { } confidence)
        {
            throw new DomainRuleViolationException(
                "ApprovalRequest.RequiresConfidence",
                "An approval request cannot be built without the stated uncertainty. A recommendation " +
                "that does not say how sure it is reads as certain.");
        }

        return new ApprovalRequest(
            opportunity.OpportunityId,
            proposal.ProposalId,
            ActionFingerprint.Of(proposal),
            opportunity.Title,
            proposal.Capability,
            proposal.ActionType,
            proposal.Target,
            proposal.Economics.EstimatedCost,
            proposal.Economics.EstimatedExposure,
            proposal.Economics.Reversibility,
            proposal.RiskTier,
            confidence,
            risk.Summary,
            risk.Factors.ToList(),
            opportunity.Evidence.Count,
            nowUtc);
    }

    public override string ToString() =>
        $"{Title}: {ActionType} on {Target}, exposure {EstimatedExposure} [{RiskTier}], {Confidence}";
}
