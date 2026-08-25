using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// The deterministic answer to "may this proposal happen?". The only thing that authorises an
/// effect to run.
/// </summary>
/// <remarks>
/// <para>
/// There is no public constructor. A decision can only be produced by the three named factories,
/// and each one binds the decision to a specific proposal identity. That binding is what stops a
/// permissive decision obtained for a harmless action being reused to authorise a different one.
/// </para>
/// <para>
/// <see cref="EvaluatedPolicies"/> records which rules fired. An audit trail that says "denied"
/// without saying which rule denied it is not much of an audit trail, and the same list is what
/// lets a human check that a policy change had the effect they intended.
/// </para>
/// </remarks>
public sealed class PolicyDecision
{
    private readonly List<string> _evaluatedPolicies;

    private PolicyDecision(
        Guid decisionId,
        Guid proposalId,
        PolicyOutcome outcome,
        string reason,
        List<string> evaluatedPolicies,
        DateTime decidedAtUtc)
    {
        DecisionId = decisionId;
        ProposalId = proposalId;
        Outcome = outcome;
        Reason = reason;
        _evaluatedPolicies = evaluatedPolicies;
        DecidedAtUtc = decidedAtUtc;
    }

    public Guid DecisionId { get; }

    /// <summary>The proposal this decision authorises. A decision is not transferable.</summary>
    public Guid ProposalId { get; }

    public PolicyOutcome Outcome { get; }

    /// <summary>Why, in terms a human reviewing the audit trail can act on.</summary>
    public string Reason { get; }

    /// <summary>Identifiers of the policy rules that were evaluated, in order.</summary>
    public IReadOnlyList<string> EvaluatedPolicies => _evaluatedPolicies;

    public DateTime DecidedAtUtc { get; }

    public bool PermitsExecution => Outcome == PolicyOutcome.Execute;

    public static PolicyDecision Execute(
        ActionProposal proposal,
        string reason,
        IEnumerable<string> evaluatedPolicies,
        DateTime nowUtc) =>
        Create(proposal, PolicyOutcome.Execute, reason, evaluatedPolicies, nowUtc);

    public static PolicyDecision RequireApproval(
        ActionProposal proposal,
        string reason,
        IEnumerable<string> evaluatedPolicies,
        DateTime nowUtc) =>
        Create(proposal, PolicyOutcome.RequireApproval, reason, evaluatedPolicies, nowUtc);

    public static PolicyDecision Deny(
        ActionProposal proposal,
        string reason,
        IEnumerable<string> evaluatedPolicies,
        DateTime nowUtc) =>
        Create(proposal, PolicyOutcome.Deny, reason, evaluatedPolicies, nowUtc);

    /// <summary>
    /// Throws unless this decision permits execution of exactly the given proposal.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="ActionExecution.Start"/>. It is the domain-level guarantee that an
    /// effect cannot run on a denied decision, on an approval-pending decision, or on a
    /// decision that was reached about some other proposal.
    /// </remarks>
    public void EnsureAuthorises(ActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (ProposalId != proposal.ProposalId)
        {
            throw new DomainRuleViolationException(
                "PolicyDecision.BoundToProposal",
                $"This decision was reached for proposal {ProposalId} and cannot authorise proposal " +
                $"{proposal.ProposalId}. A decision is not transferable between actions.");
        }

        if (Outcome != PolicyOutcome.Execute)
        {
            throw new DomainRuleViolationException(
                "PolicyDecision.ExecutionRequiresExecuteOutcome",
                $"The policy outcome for proposal {ProposalId} is {Outcome}, which does not authorise " +
                $"execution. Reason: {Reason}");
        }
    }

    public override string ToString() => $"{Outcome} ({Reason})";

    private static PolicyDecision Create(
        ActionProposal proposal,
        PolicyOutcome outcome,
        string reason,
        IEnumerable<string> evaluatedPolicies,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                nameof(reason),
                "A policy decision must state a reason. An unexplained decision cannot be reviewed.");
        }

        return new PolicyDecision(
            Guid.NewGuid(),
            proposal.ProposalId,
            outcome,
            reason.Trim(),
            evaluatedPolicies?.ToList() ?? [],
            nowUtc);
    }
}
