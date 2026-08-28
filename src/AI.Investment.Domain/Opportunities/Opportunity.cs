using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// The shared core of every opportunity, whatever type it is.
/// </summary>
/// <remarks>
/// <para>
/// The generalisation risk here runs both ways. One rigid table for stocks, suppliers and resale
/// deals models every type badly; a separate pipeline per type is three applications sharing a
/// logo. The split adopted here is an invariant core - lifecycle, economics, evidence, risk, the
/// actions it may produce - and a typed extension behind three interfaces. Adding a type means
/// implementing those three and registering a policy set; the lifecycle, approvals, audit trail,
/// capital ledger and autonomy machinery are untouched, and the new type is subject to the same
/// deterministic gate on its first day.
/// </para>
/// <para>
/// <strong>An opportunity affects the world only through an <c>ActionProposal</c>.</strong> There is
/// no method here that executes anything, spends anything, or talks to a venue. The aggregate
/// records which proposals it produced; what happens to them is the policy engine's business.
/// </para>
/// <para>
/// <strong>Uncertainty is mandatory.</strong> An opportunity cannot be evaluated without a stated
/// confidence, which is the type system enforcing §L.9 rather than a UI disclaimer that can be
/// styled away.
/// </para>
/// </remarks>
public sealed class Opportunity
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;
    public const int MaxReasonLength = 1000;

    private readonly List<ClaimId> _evidence;
    private readonly List<Guid> _proposalIds;

    private Opportunity(
        OpportunityId opportunityId,
        OpportunityType type,
        IngestionSubject subject,
        OpportunitySource source,
        string title,
        string description,
        OpportunityDetail detail,
        List<ClaimId> evidence,
        DateTime createdAtUtc)
    {
        OpportunityId = opportunityId;
        Type = type;
        Subject = subject;
        Source = source;
        Title = title;
        Description = description;
        Detail = detail;
        _evidence = evidence;
        _proposalIds = [];
        Status = OpportunityStatus.Draft;
        CreatedAtUtc = createdAtUtc;
        StatusChangedAtUtc = createdAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Opportunity()
    {
        Type = null!;
        Subject = null!;
        Source = null!;
        Title = string.Empty;
        Description = string.Empty;
        Detail = null!;
        _evidence = [];
        _proposalIds = [];
    }

    public OpportunityId OpportunityId { get; private set; }

    public OpportunityType Type { get; private set; }

    /// <summary>What the opportunity is about - the company, product or route.</summary>
    public IngestionSubject Subject { get; private set; }

    public OpportunitySource Source { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }

    /// <summary>The per-type payload. Nothing safety-relevant is read from it.</summary>
    public OpportunityDetail Detail { get; private set; }

    public OpportunityStatus Status { get; private set; }

    /// <summary>Present from <see cref="OpportunityStatus.Evaluated"/> onwards.</summary>
    public OpportunityEconomics? Economics { get; private set; }

    /// <summary>Present from <see cref="OpportunityStatus.Evaluated"/> onwards. Never optional after that.</summary>
    public OpportunityRisk? Risk { get; private set; }

    /// <summary>The stated uncertainty. Required to evaluate; §L.9 in the type system.</summary>
    public Confidence? Confidence { get; private set; }

    /// <summary>Present from <see cref="OpportunityStatus.Ranked"/> onwards.</summary>
    public OpportunityScore? Score { get; private set; }

    /// <summary>The claims this opportunity rests on. At least one is required to evaluate.</summary>
    public IReadOnlyList<ClaimId> Evidence => _evidence;

    /// <summary>The proposals this opportunity produced - the only way it reaches the world.</summary>
    public IReadOnlyList<Guid> ProposalIds => _proposalIds;

    /// <summary>The approval that permitted the action, once one has been granted.</summary>
    public Guid? ApprovalTokenId { get; private set; }

    /// <summary>The execution that carried it out, once one has succeeded.</summary>
    public Guid? ExecutionId { get; private set; }

    /// <summary>Why it was rejected or closed. Always present when it was.</summary>
    public string? Resolution { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime StatusChangedAtUtc { get; private set; }

    public bool IsTerminal =>
        Status is OpportunityStatus.Closed or OpportunityStatus.Rejected or OpportunityStatus.Expired;

    /// <summary>Discovers an opportunity. It starts in Draft, from which nothing can happen.</summary>
    public static Opportunity Draft(
        OpportunityType type,
        IngestionSubject subject,
        OpportunitySource source,
        string title,
        string description,
        OpportunityDetail detail,
        DateTime nowUtc,
        IEnumerable<ClaimId>? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(detail);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (detail.Type != type)
        {
            throw new DomainValidationException(
                nameof(detail),
                $"The detail payload is for '{detail.Type}' but the opportunity is a '{type}'. A " +
                "payload validated against the wrong schema is a payload nobody validated.");
        }

        return new Opportunity(
            OpportunityId.New(),
            type,
            subject,
            source,
            Text(title, MaxTitleLength, nameof(title)),
            Text(description, MaxDescriptionLength, nameof(description)),
            detail,
            evidence?.Distinct().ToList() ?? [],
            nowUtc);
    }

    /// <summary>Adds a claim to the evidence. Permitted only while the opportunity is a draft.</summary>
    /// <remarks>
    /// After evaluation the evidence is what the economics, the risk assessment and the score were
    /// computed from. Adding to it later would leave a stored figure attributed to evidence it never
    /// saw, which is worse than having no attribution at all.
    /// </remarks>
    public void AddEvidence(ClaimId claimId)
    {
        RequireStatus(OpportunityStatus.Draft, "add evidence");

        if (!_evidence.Contains(claimId))
        {
            _evidence.Add(claimId);
        }
    }

    /// <summary>Replaces the per-type payload. Permitted only while the opportunity is a draft.</summary>
    public void Describe(OpportunityDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        RequireStatus(OpportunityStatus.Draft, "change the detail payload");

        if (detail.Type != Type)
        {
            throw new DomainValidationException(
                nameof(detail),
                $"The detail payload is for '{detail.Type}' but the opportunity is a '{Type}'.");
        }

        Detail = detail;
    }

    /// <summary>
    /// Records the economics, the risk assessment and the stated uncertainty, and moves to
    /// <see cref="OpportunityStatus.Evaluated"/>.
    /// </summary>
    /// <remarks>
    /// The three arrive together because the opportunity is not meaningfully evaluated with any one
    /// of them missing, and permitting them to be set separately would allow a window in which it
    /// is half-evaluated and looks whole.
    /// </remarks>
    public void Evaluate(
        OpportunityEconomics economics,
        OpportunityRisk risk,
        Confidence confidence,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(economics);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(confidence);

        RequireStatus(OpportunityStatus.Draft, "evaluate");

        if (_evidence.Count == 0)
        {
            throw new DomainRuleViolationException(
                "Opportunity.EvaluationCitesEvidence",
                "An opportunity cannot be evaluated without evidence. A candidate resting on nothing " +
                "cannot be checked, and it ranks alongside ones that can.");
        }

        Economics = economics;
        Risk = risk;
        Confidence = confidence;

        MoveTo(OpportunityStatus.Evaluated, nowUtc);
    }

    /// <summary>Records the deterministic score and moves to <see cref="OpportunityStatus.Ranked"/>.</summary>
    public void Rank(OpportunityScore score, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(score);
        RequireStatus(OpportunityStatus.Evaluated, "rank");

        Score = score;

        MoveTo(OpportunityStatus.Ranked, nowUtc);
    }

    /// <summary>
    /// Records that an action has been proposed for this opportunity.
    /// </summary>
    /// <remarks>
    /// The proposal itself is created by the application layer and evaluated by the policy engine.
    /// What is recorded here is only the link, so that an opportunity can be traced to the actions
    /// it caused and an action back to the reasoning behind it.
    /// </remarks>
    public void RecordProposal(Guid proposalId, DateTime nowUtc)
    {
        if (proposalId == Guid.Empty)
        {
            throw new DomainValidationException(nameof(proposalId), "A proposal identifier is required.");
        }

        if (Status is not (OpportunityStatus.Ranked or OpportunityStatus.Proposed))
        {
            throw new DomainRuleViolationException(
                "Opportunity.ProposalRequiresRanking",
                $"An action may only be proposed for a ranked opportunity; this one is {Status}. " +
                "Proposing before ranking means acting on something never compared with anything.");
        }

        if (!_proposalIds.Contains(proposalId))
        {
            _proposalIds.Add(proposalId);
        }

        if (Status == OpportunityStatus.Ranked)
        {
            MoveTo(OpportunityStatus.Proposed, nowUtc);
        }
    }

    /// <summary>
    /// Records that a human approved the exact action presented, and moves to
    /// <see cref="OpportunityStatus.Approved"/>.
    /// </summary>
    public void Approve(Guid approvalTokenId, DateTime nowUtc)
    {
        if (approvalTokenId == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(approvalTokenId),
                "An approval must name the token that granted it, or there is no record of what was " +
                "actually approved.");
        }

        RequireStatus(OpportunityStatus.Proposed, "approve");

        ApprovalTokenId = approvalTokenId;

        MoveTo(OpportunityStatus.Approved, nowUtc);
    }

    /// <summary>Moves to <see cref="OpportunityStatus.Executing"/>.</summary>
    public void BeginExecution(DateTime nowUtc)
    {
        RequireStatus(OpportunityStatus.Approved, "begin executing");

        MoveTo(OpportunityStatus.Executing, nowUtc);
    }

    /// <summary>Records a successful execution and moves to <see cref="OpportunityStatus.Active"/>.</summary>
    public void Activate(Guid executionId, DateTime nowUtc)
    {
        if (executionId == Guid.Empty)
        {
            throw new DomainValidationException(nameof(executionId), "An execution identifier is required.");
        }

        RequireStatus(OpportunityStatus.Executing, "activate");

        ExecutionId = executionId;

        MoveTo(OpportunityStatus.Active, nowUtc);
    }

    /// <summary>Finishes the opportunity with a stated outcome.</summary>
    public void Close(string outcome, DateTime nowUtc)
    {
        if (Status is not (OpportunityStatus.Active or OpportunityStatus.Executing))
        {
            throw new DomainRuleViolationException(
                "Opportunity.CloseRequiresExecution",
                $"Only an opportunity that was acted on can be closed; this one is {Status}. Use " +
                "Reject or Expire for one that never was.");
        }

        Resolution = Text(outcome, MaxReasonLength, nameof(outcome));

        MoveTo(OpportunityStatus.Closed, nowUtc);
    }

    /// <summary>Refuses the opportunity, with a reason. Permitted from any non-terminal state.</summary>
    public void Reject(string reason, DateTime nowUtc)
    {
        RequireNotTerminal("reject");

        Resolution = Text(reason, MaxReasonLength, nameof(reason));

        MoveTo(OpportunityStatus.Rejected, nowUtc);
    }

    /// <summary>
    /// Records that the time horizon passed before the opportunity was acted on.
    /// </summary>
    /// <remarks>
    /// Distinct from rejection because the two mean different things when the hit rate is measured
    /// later: one is a decision, and the other is a decision nobody made in time.
    /// </remarks>
    public void Expire(DateTime nowUtc)
    {
        if (Status is OpportunityStatus.Active or OpportunityStatus.Executing)
        {
            throw new DomainRuleViolationException(
                "Opportunity.ActiveCannotExpire",
                $"An opportunity that is {Status} cannot expire; it has already been acted on and must " +
                "be closed with an outcome.");
        }

        RequireNotTerminal("expire");

        Resolution = "The time horizon passed before the opportunity was acted on.";

        MoveTo(OpportunityStatus.Expired, nowUtc);
    }

    public override string ToString() => $"{Type}:{Title} [{Status}]";

    private void MoveTo(OpportunityStatus status, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (nowUtc < StatusChangedAtUtc)
        {
            throw new DomainRuleViolationException(
                "Opportunity.TimeMovesForward",
                $"A status change at {nowUtc:O} is earlier than the previous one at " +
                $"{StatusChangedAtUtc:O}. A lifecycle that can move backwards in time cannot be " +
                "replayed or measured.");
        }

        Status = status;
        StatusChangedAtUtc = nowUtc;
    }

    private void RequireStatus(OpportunityStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new DomainRuleViolationException(
                "Opportunity.WrongStatus",
                $"An opportunity must be {expected} to {operation}; this one is {Status}.");
        }
    }

    private void RequireNotTerminal(string operation)
    {
        if (IsTerminal)
        {
            throw new DomainRuleViolationException(
                "Opportunity.Terminal",
                $"An opportunity that is {Status} cannot be changed. Terminal states are terminal, " +
                "or the record of what happened is not a record.");
        }
    }

    private static string Text(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, "A value is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"A value may not exceed {maxLength} characters.");
        }

        return trimmed;
    }
}
