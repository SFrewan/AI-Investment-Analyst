using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Auditing;

/// <summary>
/// One immutable entry in the append-only record of everything the system decided and did.
/// </summary>
/// <remarks>
/// <para>
/// There is no setter, no update method and no delete method on this type, and none will be
/// added. An audit trail the application can rewrite is not an audit trail. The persistence
/// layer enforces the same rule independently by refusing to persist a modified audit entity.
/// </para>
/// <para>
/// Every policy decision produces one of these, whatever the outcome - including denials, which
/// are often the most interesting entries. Reconstructing why the system refused to act is as
/// important as reconstructing why it acted.
/// </para>
/// <para>
/// <strong>Designed for what comes later.</strong> Phase 4 adds agent identity, model identity
/// and prompt version; Phase 5 adds approval identity; the validation phase adds measured
/// outcome. Those are additional fields on this record and additional
/// <see cref="AuditEventType"/> values - not a different table. The nullable identifiers below
/// are what makes that extension possible without a rewrite.
/// </para>
/// </remarks>
public sealed class AuditRecord
{
    public const int MaxSummaryLength = 1000;

    private readonly Dictionary<string, string> _details;

    private AuditRecord(
        Guid auditRecordId,
        CorrelationId correlationId,
        DateTime occurredAtUtc,
        AuditEventType eventType,
        string actor,
        ProposerKind actorKind,
        string summary,
        Dictionary<string, string> details,
        Guid? proposalId,
        Guid? decisionId,
        Guid? executionId,
        Capability? capability,
        string? actionType,
        PolicyOutcome? outcome,
        RiskTier? riskTier)
    {
        AuditRecordId = auditRecordId;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
        EventType = eventType;
        Actor = actor;
        ActorKind = actorKind;
        Summary = summary;
        _details = details;
        ProposalId = proposalId;
        DecisionId = decisionId;
        ExecutionId = executionId;
        Capability = capability;
        ActionType = actionType;
        Outcome = outcome;
        RiskTier = riskTier;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private AuditRecord()
    {
        CorrelationId = null!;
        Actor = string.Empty;
        Summary = string.Empty;
        _details = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public Guid AuditRecordId { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public AuditEventType EventType { get; private set; }

    /// <summary>Who or what caused this: a user, a service, or an agent.</summary>
    public string Actor { get; private set; }

    public ProposerKind ActorKind { get; private set; }

    public string Summary { get; private set; }

    /// <summary>
    /// Additional structured context. Must never contain a secret or personal data: these rows
    /// are permanent and cannot be redacted.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details => _details;

    public Guid? ProposalId { get; private set; }

    public Guid? DecisionId { get; private set; }

    public Guid? ExecutionId { get; private set; }

    public Capability? Capability { get; private set; }

    public string? ActionType { get; private set; }

    public PolicyOutcome? Outcome { get; private set; }

    public RiskTier? RiskTier { get; private set; }

    /// <summary>Records a policy decision. Written for every outcome, including denials.</summary>
    public static AuditRecord ForPolicyDecision(
        ActionProposal proposal,
        PolicyDecision decision,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var eventType = decision.Outcome switch
        {
            PolicyOutcome.Deny => AuditEventType.ActionDenied,
            PolicyOutcome.RequireApproval => AuditEventType.ApprovalRequired,
            _ => AuditEventType.PolicyEvaluated,
        };

        var details = BaseDetails(proposal);
        details["decision.reason"] = decision.Reason;
        details["decision.policies"] = string.Join(", ", decision.EvaluatedPolicies);

        return new AuditRecord(
            Guid.NewGuid(),
            proposal.CorrelationId,
            nowUtc,
            eventType,
            proposal.ProposedBy.Id,
            proposal.ProposedBy.Kind,
            Trim($"{decision.Outcome}: {decision.Reason}"),
            details,
            proposal.ProposalId,
            decision.DecisionId,
            executionId: null,
            proposal.Capability,
            proposal.ActionType.Value,
            decision.Outcome,
            proposal.RiskTier);
    }

    /// <summary>Records the result of attempting an authorised action's effect.</summary>
    public static AuditRecord ForExecution(
        ActionProposal proposal,
        PolicyDecision decision,
        ActionExecution execution,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(execution);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var succeeded = execution.Status == ActionExecutionStatus.Succeeded;

        var details = BaseDetails(proposal);
        details["execution.status"] = execution.Status?.ToString() ?? "Unknown";

        if (execution.FailureReason is not null)
        {
            details["execution.failureReason"] = execution.FailureReason;
        }

        return new AuditRecord(
            Guid.NewGuid(),
            proposal.CorrelationId,
            nowUtc,
            succeeded ? AuditEventType.ActionExecuted : AuditEventType.ActionFailed,
            proposal.ProposedBy.Id,
            proposal.ProposedBy.Kind,
            Trim(succeeded
                ? $"Executed {proposal.ActionType} on {proposal.Target}."
                : $"Failed {proposal.ActionType} on {proposal.Target}: {execution.FailureReason}"),
            details,
            proposal.ProposalId,
            decision.DecisionId,
            execution.ExecutionId,
            proposal.Capability,
            proposal.ActionType.Value,
            decision.Outcome,
            proposal.RiskTier);
    }

    /// <summary>Records that a repeat of an already-performed action was suppressed.</summary>
    public static AuditRecord ForDuplicateSuppressed(
        ActionProposal proposal,
        PolicyDecision decision,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var details = BaseDetails(proposal);
        details["duplicate.idempotencyKey"] = proposal.IdempotencyKey;

        return new AuditRecord(
            Guid.NewGuid(),
            proposal.CorrelationId,
            nowUtc,
            AuditEventType.DuplicateSuppressed,
            proposal.ProposedBy.Id,
            proposal.ProposedBy.Kind,
            Trim($"Suppressed duplicate {proposal.ActionType}; idempotency key already used."),
            details,
            proposal.ProposalId,
            decision.DecisionId,
            executionId: null,
            proposal.Capability,
            proposal.ActionType.Value,
            decision.Outcome,
            proposal.RiskTier);
    }

    private static Dictionary<string, string> BaseDetails(ActionProposal proposal)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proposal.target"] = proposal.Target.ToString(),
            ["proposal.parameters"] = proposal.Parameters.Describe(),
            ["proposal.economics"] = proposal.Economics.ToString(),
            ["proposal.idempotencyKey"] = proposal.IdempotencyKey,
            ["proposer"] = proposal.ProposedBy.ToString(),
        };

        if (proposal.Confidence is not null)
        {
            details["proposal.confidence"] = proposal.Confidence.ToString();
        }

        if (proposal.Evidence.Count > 0)
        {
            details["proposal.evidenceCount"] = proposal.Evidence.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (proposal.ProposedBy.PromptId is not null)
        {
            details["proposer.prompt"] = $"{proposal.ProposedBy.PromptId}@{proposal.ProposedBy.PromptVersion}";
        }

        return details;
    }

    private static string Trim(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainValidationException(nameof(summary), "An audit record requires a summary.");
        }

        var trimmed = summary.Trim();

        return trimmed.Length <= MaxSummaryLength ? trimmed : trimmed[..MaxSummaryLength];
    }
}
