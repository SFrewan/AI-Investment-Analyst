using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// A request to do something that has an effect on the world. Immutable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every side effect in this system is expressed as one of these.</strong> Not just the
/// financial ones - creating a company record, sending an email, spending money on an LLM call
/// and placing an order are all proposals, and all pass the same gate. That uniformity is the
/// entire point: a safety control that applies to "the dangerous paths" is a control that
/// depends on someone correctly identifying which paths those are.
/// </para>
/// <para>
/// A proposal is a request, never a decision. It confers no authority. The only thing that
/// permits an effect to run is a <see cref="PolicyDecision"/> with outcome
/// <see cref="PolicyOutcome.Execute"/>, produced by <see cref="PolicyEngine"/> from this
/// proposal.
/// </para>
/// <para>
/// Note what the proposer cannot set: <see cref="RiskTier"/> is computed by
/// <see cref="RiskTierCalculator"/> during construction. There is no constructor overload that
/// accepts it.
/// </para>
/// </remarks>
public sealed class ActionProposal
{
    public const int MaxIdempotencyKeyLength = 200;

    private readonly List<ClaimId> _evidence;

    private ActionProposal(
        Guid proposalId,
        CorrelationId correlationId,
        Guid? cycleId,
        Capability capability,
        ActionType actionType,
        ActionTarget target,
        IActionParameters parameters,
        ActionEconomics economics,
        RiskTier riskTier,
        bool isNovel,
        ProposedBy proposedBy,
        List<ClaimId> evidence,
        Confidence? confidence,
        string idempotencyKey,
        DateTime createdAtUtc)
    {
        ProposalId = proposalId;
        CorrelationId = correlationId;
        CycleId = cycleId;
        Capability = capability;
        ActionType = actionType;
        Target = target;
        Parameters = parameters;
        Economics = economics;
        RiskTier = riskTier;
        IsNovel = isNovel;
        ProposedBy = proposedBy;
        _evidence = evidence;
        Confidence = confidence;
        IdempotencyKey = idempotencyKey;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid ProposalId { get; }

    /// <summary>Threads this proposal to the request or cycle that caused it.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>
    /// The autonomous operating cycle this proposal belongs to, when one exists. Always null in
    /// Phase 1: cycles arrive with continuous operation.
    /// </summary>
    public Guid? CycleId { get; }

    public Capability Capability { get; }

    public ActionType ActionType { get; }

    public ActionTarget Target { get; }

    /// <summary>The typed payload. Never read by the policy engine - see <see cref="IActionParameters"/>.</summary>
    public IActionParameters Parameters { get; }

    public ActionEconomics Economics { get; }

    /// <summary>Computed at construction from capability, reversibility and exposure. Never supplied.</summary>
    public RiskTier RiskTier { get; }

    public bool IsNovel { get; }

    public ProposedBy ProposedBy { get; }

    /// <summary>The claims this proposal rests on. Required when the proposer is an AI agent.</summary>
    public IReadOnlyList<ClaimId> Evidence => _evidence;

    /// <summary>Stated confidence. Required when the proposer is an AI agent.</summary>
    public Confidence? Confidence { get; }

    /// <summary>
    /// Deduplication key. Two proposals with the same key describe the same intended effect, and
    /// only the first one executes.
    /// </summary>
    /// <remarks>
    /// Retries are the normal case in an unattended system, not the exception. "It retried and
    /// bought twice" is the most likely way this platform first loses real money - ahead of any
    /// failure of analysis - which is why the key lives on the proposal rather than being added
    /// in the execution layer later.
    /// </remarks>
    public string IdempotencyKey { get; }

    public DateTime CreatedAtUtc { get; }

    public static ActionProposal Create(
        CorrelationId correlationId,
        Capability capability,
        ActionType actionType,
        ActionTarget target,
        IActionParameters parameters,
        ActionEconomics economics,
        ProposedBy proposedBy,
        string idempotencyKey,
        DateTime nowUtc,
        Guid? cycleId = null,
        IEnumerable<ClaimId>? evidence = null,
        Confidence? confidence = null,
        bool isNovel = false)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(actionType);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(economics);
        ArgumentNullException.ThrowIfNull(proposedBy);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        var key = ValidateIdempotencyKey(idempotencyKey);
        var evidenceList = evidence?.ToList() ?? [];

        // An AI proposer must show its work. Both rules exist so that a model's output cannot
        // enter the pipeline as a bare assertion: without evidence there is nothing to check
        // groundedness against, and without confidence the proposal is indistinguishable
        // downstream from a deterministic calculation.
        if (proposedBy.IsAi)
        {
            if (confidence is null)
            {
                throw new DomainRuleViolationException(
                    "ActionProposal.AiStatesConfidence",
                    "A proposal from an AI agent must state its confidence.");
            }

            if (evidenceList.Count == 0)
            {
                throw new DomainRuleViolationException(
                    "ActionProposal.AiCitesEvidence",
                    "A proposal from an AI agent must cite the evidence it rests on. A proposal with no " +
                    "traceable supporting claim cannot be checked and must be treated as unfounded.");
            }
        }

        var riskTier = RiskTierCalculator.Calculate(capability, economics, isNovel);

        return new ActionProposal(
            Guid.NewGuid(),
            correlationId,
            cycleId,
            capability,
            actionType,
            target,
            parameters,
            economics,
            riskTier,
            isNovel,
            proposedBy,
            evidenceList,
            confidence,
            key,
            nowUtc);
    }

    public override string ToString() =>
        $"{ActionType} on {Target} [{Capability}/{RiskTier}] by {ProposedBy}";

    private static string ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainValidationException(
                nameof(idempotencyKey),
                "An idempotency key is required. Without one, a retry cannot be told apart from a second " +
                "intended action.");
        }

        var trimmed = idempotencyKey.Trim();

        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new DomainValidationException(
                nameof(idempotencyKey),
                $"An idempotency key may not exceed {MaxIdempotencyKeyLength} characters.");
        }

        return trimmed;
    }
}
