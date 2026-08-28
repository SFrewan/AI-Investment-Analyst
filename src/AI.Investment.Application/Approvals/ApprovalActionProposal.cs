using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Approvals;

/// <summary>
/// The proposal for the act of approving something.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Issuing an approval token is itself a side effect, so it goes through the seam.</strong>
/// The architecture's rule is "every side effect, without exception" - not "every financial one" -
/// and an approval is the single most consequential non-financial write in the platform: it is the
/// record that turns a proposal into something the executor will act on. Routing it through the
/// gateway means it is policy-evaluated, idempotent and audited like anything else, and it means
/// the write reaches the database inside an authorisation window rather than beside one.
/// </para>
/// <para>
/// The capability is <see cref="Capability.ApprovalAdministration"/>, which the policy engine
/// refuses to an AI proposer by a structural rule evaluated before any configuration. That is the
/// point of using it here: a model cannot approve its own proposal, and the prohibition is not a
/// setting anyone can change.
/// </para>
/// <para>
/// The idempotency key is derived from the proposal being approved, so approving the same action
/// twice is suppressed rather than issuing a second token for it.
/// </para>
/// </remarks>
public static class ApprovalActionProposal
{
    public static ActionType Type { get; } = ActionType.Create("approval.issue-token");

    public static ActionProposal For(
        ApprovalRequest request,
        string approvedBy,
        CorrelationId correlationId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ActionProposal.Create(
            correlationId,
            Capability.ApprovalAdministration,
            Type,
            ActionTarget.Create("Opportunity", request.OpportunityId.ToString()),
            new ApprovalParameters(
                request.OpportunityId.Value,
                request.ProposalId,
                request.Fingerprint.Value,
                approvedBy),
            ActionEconomics.NoFinancialEffect(request.EstimatedExposure.Currency),
            ProposedBy.Human(approvedBy),
            "approval:" + request.ProposalId.ToString("n", System.Globalization.CultureInfo.InvariantCulture),
            nowUtc);
    }

    /// <summary>The proposal for withdrawing an approval before it is used.</summary>
    public static ActionType RevocationType { get; } = ActionType.Create("approval.revoke-token");

    public static ActionProposal ForRevocation(
        Domain.Approvals.ApprovalToken token,
        string reason,
        CorrelationId correlationId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(token);

        return ActionProposal.Create(
            correlationId,
            Capability.ApprovalAdministration,
            RevocationType,
            ActionTarget.Create("Opportunity", token.OpportunityId.ToString()),
            new RevocationParameters(token.ApprovalTokenId, reason ?? string.Empty),
            ActionEconomics.NoFinancialEffect(token.MaxAmount.Currency),
            ProposedBy.Service("approval-workflow", "1.0"),
            "approval-revoke:" + token.ApprovalTokenId.ToString("n", System.Globalization.CultureInfo.InvariantCulture),
            nowUtc);
    }

    private sealed record RevocationParameters(Guid ApprovalTokenId, string Reason) : IActionParameters
    {
        public string Describe() => $"token={ApprovalTokenId}, reason='{Reason}'";
    }

    private sealed record ApprovalParameters(
        Guid OpportunityId,
        Guid ProposalId,
        string Fingerprint,
        string ApprovedBy) : IActionParameters
    {
        public string Describe() =>
            $"opportunity={OpportunityId}, proposal={ProposalId}, action={Fingerprint}, by='{ApprovedBy}'";
    }
}

/// <summary>What happened when a person tried to approve an action.</summary>
public sealed record ApprovalOutcome
{
    private ApprovalOutcome(ApprovalOutcomeStatus status, string explanation, Domain.Approvals.ApprovalToken? token)
    {
        Status = status;
        Explanation = explanation;
        Token = token;
    }

    public ApprovalOutcomeStatus Status { get; }

    public string Explanation { get; }

    public Domain.Approvals.ApprovalToken? Token { get; }

    public bool Issued => Status == ApprovalOutcomeStatus.Issued;

    public static ApprovalOutcome ForIssued(Domain.Approvals.ApprovalToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new ApprovalOutcome(ApprovalOutcomeStatus.Issued, "The approval was issued.", token);
    }

    public static ApprovalOutcome ForRevoked(Domain.Approvals.ApprovalToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new ApprovalOutcome(ApprovalOutcomeStatus.Revoked, "The approval was revoked.", token);
    }

    public static ApprovalOutcome Refused(ApprovalOutcomeStatus status, string explanation) =>
        new(status, explanation, null);

    public override string ToString() => $"{Status}: {Explanation}";
}

/// <summary>
/// The outcomes of an approval attempt. <see cref="Unknown"/> is zero and is a failure, so a
/// result that skipped initialisation cannot present itself as an issued approval.
/// </summary>
public enum ApprovalOutcomeStatus
{
    Unknown = 0,
    Issued = 1,
    DeniedByPolicy = 2,
    EscalationRequired = 3,
    DuplicateSuppressed = 4,
    Revoked = 5,
}
