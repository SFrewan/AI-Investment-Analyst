using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// The record that an approved action's effect was attempted, and what happened.
/// </summary>
/// <remarks>
/// <para>
/// An execution cannot be created without a <see cref="PolicyDecision"/> that authorises the
/// exact proposal it belongs to - <see cref="Start"/> calls
/// <see cref="PolicyDecision.EnsureAuthorises"/> and throws otherwise. This is the domain-level
/// half of the guarantee that a write cannot bypass the gate; the other half lives in the
/// persistence layer, which refuses to save anything unless an authorised execution is in
/// progress. Two independent mechanisms, because a single one can be forgotten at a call site.
/// </para>
/// <para>
/// Append-only in spirit: the only permitted transition is from started to completed, exactly
/// once. There is no method that reopens or edits a finished execution.
/// </para>
/// </remarks>
public sealed class ActionExecution
{
    public const int MaxFailureReasonLength = 2000;

    private ActionExecution(
        Guid executionId,
        Guid proposalId,
        Guid decisionId,
        CorrelationId correlationId,
        ActionType actionType,
        Capability capability,
        string idempotencyKey,
        DateTime startedAtUtc)
    {
        ExecutionId = executionId;
        ProposalId = proposalId;
        DecisionId = decisionId;
        CorrelationId = correlationId;
        ActionType = actionType;
        Capability = capability;
        IdempotencyKey = idempotencyKey;
        StartedAtUtc = startedAtUtc;
    }

    public Guid ExecutionId { get; }

    public Guid ProposalId { get; }

    /// <summary>The decision that authorised this execution. Never null: there is no unauthorised path.</summary>
    public Guid DecisionId { get; }

    public CorrelationId CorrelationId { get; }

    public ActionType ActionType { get; }

    public Capability Capability { get; }

    public string IdempotencyKey { get; }

    public DateTime StartedAtUtc { get; }

    public DateTime? CompletedAtUtc { get; private set; }

    public ActionExecutionStatus? Status { get; private set; }

    /// <summary>Why it failed, if it did. Never contains a stack trace or a secret.</summary>
    public string? FailureReason { get; private set; }

    public bool IsComplete => Status is not null;

    /// <summary>
    /// Begins an execution. Throws unless the decision authorises this exact proposal to execute.
    /// </summary>
    public static ActionExecution Start(ActionProposal proposal, PolicyDecision decision, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        // The one line that makes the seam real.
        decision.EnsureAuthorises(proposal);

        return new ActionExecution(
            Guid.NewGuid(),
            proposal.ProposalId,
            decision.DecisionId,
            proposal.CorrelationId,
            proposal.ActionType,
            proposal.Capability,
            proposal.IdempotencyKey,
            nowUtc);
    }

    public void MarkSucceeded(DateTime nowUtc)
    {
        EnsureNotAlreadyComplete(nowUtc);

        Status = ActionExecutionStatus.Succeeded;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records a failure.
    /// </summary>
    /// <param name="reason">
    /// A short description. Callers must pass a message safe to store permanently - the audit
    /// trail is append-only and cannot be redacted, so exception detail and anything
    /// credential-shaped stays out of it.
    /// </param>
    public void MarkFailed(string reason, DateTime nowUtc)
    {
        EnsureNotAlreadyComplete(nowUtc);

        Status = ActionExecutionStatus.Failed;
        CompletedAtUtc = nowUtc;
        FailureReason = Truncate(reason);
    }

    public override string ToString() =>
        $"{ActionType} [{Status?.ToString() ?? "in-flight"}] proposal={ProposalId} decision={DecisionId}";

    private void EnsureNotAlreadyComplete(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (IsComplete)
        {
            throw new DomainRuleViolationException(
                "ActionExecution.CompletesOnce",
                $"Execution {ExecutionId} has already completed with status {Status}. An execution record " +
                "is written once and never revised.");
        }

        if (nowUtc < StartedAtUtc)
        {
            throw new DomainRuleViolationException(
                "ActionExecution.CompletionFollowsStart",
                $"An execution cannot complete ({nowUtc:O}) before it started ({StartedAtUtc:O}).");
        }
    }

    private static string Truncate(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "No reason supplied.";
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxFailureReasonLength
            ? trimmed
            : trimmed[..MaxFailureReasonLength];
    }
}
