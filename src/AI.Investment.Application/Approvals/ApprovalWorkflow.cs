using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Approvals;

/// <summary>
/// Presents an action for a human decision, and turns that decision into a bound, expiring token.
/// </summary>
/// <remarks>
/// <para>
/// Assembling the request is deterministic service work rather than agent work: it is a structured
/// record built from an opportunity, and every figure in it was calculated somewhere that can be
/// re-run. A model has no part in it.
/// </para>
/// <para>
/// <strong>Issuing the token goes through the action gateway</strong> (§H.3: every side effect,
/// without exception). Three things follow from that and none of them are incidental: the write
/// happens inside an authorisation window, so the persistence guard permits it; the act of
/// approving is audited with the same record shape as everything else; and the policy engine's
/// structural rule refusing <c>ApprovalAdministration</c> to an AI proposer stands between a model
/// and its own approval.
/// </para>
/// <para>
/// The token is constructed before dispatch and stored inside the effect. Constructing it is where
/// the domain's own refusals fire - an unattributed approver, a ceiling in the wrong currency, a
/// window of zero - and those are the caller's mistakes to fix, not policy decisions.
/// </para>
/// </remarks>
public sealed class ApprovalWorkflow
{
    /// <summary>
    /// How long an approval is good for when the caller does not say.
    /// </summary>
    /// <remarks>
    /// Short on purpose. A stale market context makes yesterday's approval a different decision
    /// from the one the person actually evaluated.
    /// </remarks>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(4);

    private readonly IActionGateway _gateway;
    private readonly IOpportunityRepository _opportunities;
    private readonly IApprovalTokenStore _tokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public ApprovalWorkflow(
        IActionGateway gateway,
        IOpportunityRepository opportunities,
        IApprovalTokenStore tokens,
        IUnitOfWork unitOfWork,
        ICorrelationContext correlation,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _opportunities = opportunities ?? throw new ArgumentNullException(nameof(opportunities));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Builds what a person is shown before deciding.</summary>
    public ApprovalRequest Present(Opportunity opportunity, ActionProposal proposal) =>
        ApprovalRequest.For(opportunity, proposal, _clock.UtcNow);

    /// <summary>Records a human decision to permit exactly the action that was presented.</summary>
    public async Task<ApprovalOutcome> ApproveAsync(
        ApprovalRequest request,
        ActionProposal proposal,
        string approvedBy,
        Money maxAmount,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(maxAmount);

        if (!request.Fingerprint.Matches(proposal))
        {
            throw new DomainRuleViolationException(
                "ApprovalWorkflow.ActionChanged",
                "The action has changed since it was presented for approval. A token issued now would " +
                "authorise something the approver never saw.");
        }

        if (request.ProposalId != proposal.ProposalId)
        {
            throw new DomainRuleViolationException(
                "ApprovalWorkflow.WrongProposal",
                "The approval request describes a different proposal from the one being approved.");
        }

        if (maxAmount.Currency == request.EstimatedExposure.Currency &&
            maxAmount.IsGreaterThan(request.EstimatedExposure))
        {
            throw new DomainRuleViolationException(
                "ApprovalWorkflow.CeilingAboveWhatWasShown",
                $"The approval ceiling of {maxAmount} is above the {request.EstimatedExposure} exposure " +
                "presented. A ceiling higher than the figure on the screen authorises something nobody " +
                "read.");
        }

        var nowUtc = _clock.UtcNow;

        var token = ApprovalToken.Issue(
            request.OpportunityId,
            proposal,
            maxAmount,
            approvedBy,
            nowUtc,
            validFor ?? DefaultValidity);

        var approvalProposal = ApprovalActionProposal.For(
            request,
            approvedBy,
            _correlation.Current,
            nowUtc);

        var outcome = await _gateway.DispatchAsync(
            approvalProposal,
            async effectToken =>
            {
                await _tokens.AddAsync(token, effectToken).ConfigureAwait(false);

                var opportunity = await _opportunities
                    .GetAsync(request.OpportunityId, effectToken)
                    .ConfigureAwait(false);

                if (opportunity is not null)
                {
                    opportunity.Approve(token.ApprovalTokenId, nowUtc);

                    await _opportunities.AddAsync(opportunity, effectToken).ConfigureAwait(false);
                    await _unitOfWork.SaveChangesAsync(effectToken).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => ApprovalOutcome.ForIssued(token),
            ActionOutcomeStatus.Denied =>
                ApprovalOutcome.Refused(ApprovalOutcomeStatus.DeniedByPolicy, outcome.Reason),
            ActionOutcomeStatus.ApprovalRequired =>
                ApprovalOutcome.Refused(ApprovalOutcomeStatus.EscalationRequired, outcome.Reason),
            ActionOutcomeStatus.DuplicateSuppressed =>
                ApprovalOutcome.Refused(ApprovalOutcomeStatus.DuplicateSuppressed, outcome.Reason),
            _ => ApprovalOutcome.Refused(ApprovalOutcomeStatus.DeniedByPolicy, outcome.Reason),
        };
    }

    /// <summary>
    /// Withdraws an approval that has not been used.
    /// </summary>
    /// <remarks>
    /// Revocation is routed through the seam for the same reason issuing is: it changes what the
    /// executor is permitted to do, and a change to permission that leaves no audit record is a
    /// change nobody can reconstruct.
    /// </remarks>
    public async Task<ApprovalOutcome> RevokeAsync(
        Guid approvalTokenId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var token = await _tokens.GetAsync(approvalTokenId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainRuleViolationException(
                "ApprovalWorkflow.UnknownToken",
                $"No approval token {approvalTokenId} exists to revoke.");

        var nowUtc = _clock.UtcNow;

        token.Revoke(reason, nowUtc);

        var revocation = ApprovalActionProposal.ForRevocation(
            token,
            reason,
            _correlation.Current,
            nowUtc);

        var outcome = await _gateway.DispatchAsync(
            revocation,
            async effectToken =>
            {
                await _tokens.AddAsync(token, effectToken).ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status == ActionOutcomeStatus.Executed
            ? ApprovalOutcome.ForRevoked(token)
            : ApprovalOutcome.Refused(ApprovalOutcomeStatus.DeniedByPolicy, outcome.Reason);
    }
}
