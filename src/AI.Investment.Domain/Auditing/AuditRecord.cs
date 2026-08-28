using System.Globalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
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

    /// <summary>
    /// Records what an agent produced, or refused to produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An agent run is not an action and never becomes one: nothing here touches a proposal, a
    /// decision or an execution, and the three identifier columns stay null. What is recorded is
    /// everything needed to reproduce the run and to notice when it stops being reproducible - the
    /// model and its pinned version, the prompt and its version, the fingerprint of the evidence
    /// the agent was shown, and what the run cost.
    /// </para>
    /// <para>
    /// The evidence hash is the field that makes the rest worth storing. Without it, two analyses
    /// of the same company a month apart differ for reasons nobody can separate; with it, "the
    /// evidence changed" and "the answer changed" are distinguishable in the data.
    /// </para>
    /// </remarks>
    public static AuditRecord ForAgentRun(
        CorrelationId correlationId,
        IngestionSubject subject,
        string evidenceHash,
        AgentResult result,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(result);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(evidenceHash))
        {
            throw new DomainValidationException(
                nameof(evidenceHash),
                "An agent run must record the fingerprint of the evidence it was shown, or the run " +
                "cannot be reproduced and its output cannot be compared with any other.");
        }

        var diagnostics = result.Diagnostics;

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["analysis.subject"] = subject.ToString(),
            ["analysis.evidenceHash"] = evidenceHash.Trim(),
            ["agent.id"] = result.AgentId.Value,
            ["agent.version"] = result.AgentVersion,
            ["agent.status"] = result.Status.ToString(),
            ["model"] = diagnostics.Model.ToString(),
            ["prompt"] = diagnostics.Prompt.ToString(),
            ["run.tokensIn"] = Number(diagnostics.TokensIn),
            ["run.tokensOut"] = Number(diagnostics.TokensOut),
            ["run.costUsd"] = diagnostics.CostUsd.ToString("0.######", CultureInfo.InvariantCulture),
            ["run.latencyMs"] = Number(diagnostics.LatencyMs),
            ["run.attempts"] = Number(diagnostics.Attempts),
            ["run.evidenceCount"] = Number(result.Evidence.Count),
        };

        if (result.Confidence is not null)
        {
            details["agent.confidence"] = result.Confidence.ToString();
        }

        if (result.Limitations.Count > 0)
        {
            details["agent.limitations"] = string.Join(" | ", result.Limitations);
        }

        if (result.Explanation is not null)
        {
            details["agent.explanation"] = result.Explanation;
        }

        return new AuditRecord(
            Guid.NewGuid(),
            correlationId,
            nowUtc,
            result.Succeeded ? AuditEventType.AgentOutputAccepted : AuditEventType.AgentOutputRejected,
            result.AgentId.Value,
            ProposerKind.AiAgent,
            Trim(result.Succeeded
                ? $"{result.AgentId} analysed {subject} at {result.Confidence}."
                : $"{result.AgentId} produced nothing for {subject}: {result.Status} - {result.Explanation}"),
            details,
            proposalId: null,
            decisionId: null,
            executionId: null,
            capability: null,
            actionType: null,
            outcome: null,
            riskTier: null);
    }

    /// <summary>
    /// Records something the operating loop did: a cycle starting or stopping, a watch firing, an
    /// escalation, a grant changing, a shadow measurement, a queued message delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One factory rather than a dozen, because these events share a shape: they are things the
    /// platform did to itself while nobody was watching, and the question asked of them afterwards
    /// is always "what happened, to what, and when". The three action identifiers stay null - a
    /// cycle is not a proposal - and the cycle identifier travels in <see cref="Details"/> under a
    /// stable key so a whole cycle can be reconstructed from the trail.
    /// </para>
    /// <para>
    /// Unattended operation is exactly the condition under which an audit trail stops being a
    /// nicety. When something goes wrong overnight, this is the only account of it that exists.
    /// </para>
    /// </remarks>
    public static AuditRecord ForOperation(
        CorrelationId correlationId,
        AuditEventType eventType,
        string actor,
        string summary,
        DateTime nowUtc,
        Guid? cycleId = null,
        Capability? capability = null,
        IEnumerable<KeyValuePair<string, string>>? details = null)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainValidationException(
                nameof(actor),
                "An operation record must name what did it. 'Something happened' is not an audit trail.");
        }

        var payload = new Dictionary<string, string>(StringComparer.Ordinal);

        if (cycleId is not null)
        {
            payload["cycle.id"] = cycleId.Value.ToString("d", CultureInfo.InvariantCulture);
        }

        if (details is not null)
        {
            foreach (var entry in details)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                payload[entry.Key] = entry.Value ?? string.Empty;
            }
        }

        return new AuditRecord(
            Guid.NewGuid(),
            correlationId,
            nowUtc,
            eventType,
            actor.Trim(),
            ProposerKind.DeterministicService,
            Trim(summary),
            payload,
            proposalId: null,
            decisionId: null,
            executionId: null,
            capability: capability,
            actionType: null,
            outcome: null,
            riskTier: null);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

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
