using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Watching;

namespace AI.Investment.Application.Operators;

/// <summary>
/// The operations a named, authenticated person may perform on a running platform.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6 recorded why these did not exist: *"an endpoint that resolved an escalation without
/// knowing who was calling would make the record of who decided a fiction."* This class is the
/// answer to that, and the identity is the whole point of it. Every action here is proposed by
/// <see cref="ProposedBy.Human"/> carrying the operator's own identifier, so the audit record's
/// actor is a person rather than a service, and the question "who decided this?" has an answer.
/// </para>
/// <para>
/// <strong>Nothing here is a shortcut.</strong> Every action is an <see cref="ActionProposal"/>
/// dispatched through the same gateway as everything else: policy evaluated, kill-switch checked,
/// idempotency claimed, audited before the effect and again after it, and written inside an
/// authorisation window. There is no method on this class that touches a repository outside a
/// dispatched effect. An operator surface that bypassed the seam would be the largest hole this
/// platform could acquire, because it would be the one path a person is expected to use.
/// </para>
/// <para>
/// <strong>Authorisation is checked twice, in different places, on purpose.</strong> The API refuses
/// the request when the caller's principal lacks the privilege; this class refuses it again from
/// the identity it was handed. The two are not redundant: the first is transport policy and could be
/// forgotten on a new endpoint, and the second is the rule itself and is enforced for every caller
/// including a future one that is not HTTP.
/// </para>
/// <para>
/// <strong>What is absent.</strong> There is no approve. An approval token binds to the identity of
/// the exact proposal a person was shown, proposals are not persisted, and a second request
/// rebuilding "the same" proposal produces a different identity - so an approve endpoint would
/// either refuse every token or would have to loosen the binding that makes a token mean anything.
/// That is Phase 5's recorded limitation and its prerequisite is persisting proposals, which is a
/// schema change and its own piece of work. Rejecting needs no token and is here.
/// </para>
/// </remarks>
public sealed class OperatorConsole
{
    public const string ServiceId = "application.operators.console";

    private readonly IActionGateway _gateway;
    private readonly IOperatorContext _operators;
    private readonly ICorrelationContext _correlation;
    private readonly IOpportunityRepository _opportunities;
    private readonly IEscalationStore _escalations;
    private readonly IWatchStore _watches;
    private readonly IKillSwitchAdministration _killSwitch;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public OperatorConsole(
        IActionGateway gateway,
        IOperatorContext operators,
        ICorrelationContext correlation,
        IOpportunityRepository opportunities,
        IEscalationStore escalations,
        IWatchStore watches,
        IKillSwitchAdministration killSwitch,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _opportunities = opportunities ?? throw new ArgumentNullException(nameof(opportunities));
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _watches = watches ?? throw new ArgumentNullException(nameof(watches));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Refuses an opportunity the platform put forward, with a reason that is kept.</summary>
    public async Task<OperatorOutcome> RejectOpportunityAsync(
        Guid opportunityId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Authorise(OperatorPrivilege.DecideOpportunities) is not { } identity)
        {
            return Refusal(OperatorPrivilege.DecideOpportunities);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperatorOutcome.Refused(
                "A rejection must state a reason. An opportunity refused without one cannot be " +
                "measured against the ones that were acted on.");
        }

        var opportunity = await _opportunities
            .GetAsync(OpportunityId.Create(opportunityId), cancellationToken)
            .ConfigureAwait(false);

        if (opportunity is null)
        {
            return OperatorOutcome.NotFound($"No opportunity {opportunityId} exists.");
        }

        if (opportunity.IsTerminal)
        {
            return OperatorOutcome.Refused(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This opportunity is already {opportunity.Status}. Terminal states are terminal, " +
                    $"or the record of what happened is not a record."));
        }

        return await DispatchAsync(
            identity,
            Capability.OpportunityManagement,
            OperatorActionTypes.RejectOpportunity,
            ActionTarget.Create("Opportunity", opportunityId.ToString()),
            new OperatorActionParameters("Reject opportunity", opportunity.Title, reason.Trim()),
            string.Create(CultureInfo.InvariantCulture, $"operator.reject-opportunity:{opportunityId}"),
            async token =>
            {
                opportunity.Reject(reason, _clock.UtcNow);

                await _opportunities.AddAsync(opportunity, token).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

                return $"Rejected opportunity {opportunityId}.";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that a person has seen an escalation and is dealing with it.</summary>
    public Task<OperatorOutcome> AcknowledgeEscalationAsync(
        Guid escalationId,
        CancellationToken cancellationToken = default) =>
        AnswerEscalationAsync(
            escalationId,
            OperatorActionTypes.AcknowledgeEscalation,
            "Acknowledge escalation",
            resolution: null,
            cancellationToken);

    /// <summary>Records that an escalation has been dealt with, and how.</summary>
    public Task<OperatorOutcome> ResolveEscalationAsync(
        Guid escalationId,
        string resolution,
        CancellationToken cancellationToken = default) =>
        AnswerEscalationAsync(
            escalationId,
            OperatorActionTypes.ResolveEscalation,
            "Resolve escalation",
            resolution,
            cancellationToken);

    /// <summary>
    /// Engages the kill switch. One way: nothing here disengages it.
    /// </summary>
    /// <remarks>
    /// When the switch is already engaged the policy engine denies this proposal, and that is the
    /// right answer rather than a defect - the caller wanted the switch on, and it is on. The denial
    /// is audited like any other, so a second attempt during an incident is visible afterwards.
    /// </remarks>
    public async Task<OperatorOutcome> EngageKillSwitchAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Authorise(OperatorPrivilege.AdministerKillSwitch) is not { } identity)
        {
            return Refusal(OperatorPrivilege.AdministerKillSwitch);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperatorOutcome.Refused(
                "Engaging the kill switch must state a reason. Whoever finds it engaged needs to " +
                "know what stopped, and why, before deciding whether to start it again.");
        }

        var now = _clock.UtcNow;

        return await DispatchAsync(
            identity,
            Capability.PolicyAdministration,
            OperatorActionTypes.EngageKillSwitch,
            ActionTarget.Create("KillSwitch", "global"),
            new OperatorActionParameters("Engage kill switch", "global", reason.Trim()),

            // Keyed to the minute rather than to the switch. A double-click is suppressed; engaging
            // again after somebody disengaged it out of band is a different act and must not be.
            string.Create(
                CultureInfo.InvariantCulture,
                $"operator.engage-kill-switch:{identity.Id}:{now:yyyyMMddTHHmm}"),
            async token =>
            {
                await _killSwitch
                    .EngageAsync(capability: null, reason.Trim(), _clock.UtcNow, token)
                    .ConfigureAwait(false);

                return "Kill switch engaged.";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a scheduled watch on an instrument, which is how the observation window is pointed at
    /// something.
    /// </summary>
    public async Task<OperatorOutcome> CreateScheduledWatchAsync(
        ScheduledWatchDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (Authorise(OperatorPrivilege.AdministerWatches) is not { } identity)
        {
            return Refusal(OperatorPrivilege.AdministerWatches);
        }

        Watch watch;

        try
        {
            watch = Watch.Create(
                definition.Name,
                WatchTarget.Create(definition.TargetKind, definition.TargetIdentifier),
                TriggerType.Schedule,
                TriggerCondition.Every(definition.Interval),
                definition.Cooldown,
                definition.Capability,
                definition.CycleTemplate,
                _clock.UtcNow);
        }
        catch (DomainException exception)
        {
            // The domain's own refusals - a cooldown below the minimum, an interval of zero, an
            // unrecognised capability - are the caller's mistakes to fix. They are reported rather
            // than proposed, because proposing an action that cannot be built would audit an
            // intention that never existed.
            return OperatorOutcome.Refused(exception.Message);
        }

        return await DispatchAsync(
            identity,
            Capability.ReferenceDataManagement,
            OperatorActionTypes.CreateWatch,
            ActionTarget.Create(definition.TargetKind, definition.TargetIdentifier),
            new OperatorActionParameters(
                "Create scheduled watch",
                definition.Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"every {definition.Interval}, cooldown {definition.Cooldown}, " +
                    $"template '{definition.CycleTemplate}' under {definition.Capability}")),
            string.Create(
                CultureInfo.InvariantCulture,
                $"operator.create-watch:{definition.TargetKind}:{definition.TargetIdentifier}:" +
                $"{definition.CycleTemplate}"),
            async token =>
            {
                await _watches.AddAsync(watch, token).ConfigureAwait(false);
                await _watches.SaveAsync(token).ConfigureAwait(false);

                return $"Watch {watch.WatchId} created.";
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperatorOutcome> AnswerEscalationAsync(
        Guid escalationId,
        ActionType actionType,
        string verb,
        string? resolution,
        CancellationToken cancellationToken)
    {
        if (Authorise(OperatorPrivilege.AnswerEscalations) is not { } identity)
        {
            return Refusal(OperatorPrivilege.AnswerEscalations);
        }

        var resolving = actionType == OperatorActionTypes.ResolveEscalation;

        if (resolving && string.IsNullOrWhiteSpace(resolution))
        {
            return OperatorOutcome.Refused(
                "Resolving an escalation must say what was done about it. A resolution nobody wrote " +
                "down is indistinguishable from an escalation nobody answered.");
        }

        var escalation = await _escalations.FindAsync(escalationId, cancellationToken).ConfigureAwait(false);

        if (escalation is null)
        {
            return OperatorOutcome.NotFound($"No escalation {escalationId} exists.");
        }

        if (escalation.IsResolved)
        {
            return OperatorOutcome.Refused("This escalation has already been resolved.");
        }

        if (!resolving && escalation.IsAcknowledged)
        {
            return OperatorOutcome.Refused("This escalation has already been acknowledged.");
        }

        return await DispatchAsync(
            identity,
            escalation.Capability,
            actionType,
            ActionTarget.Create("Escalation", escalationId.ToString()),
            new OperatorActionParameters(
                verb,
                escalation.Reason.ToString(),
                resolution?.Trim() ?? "acknowledged"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{actionType.Value}:{escalationId}"),
            async token =>
            {
                if (resolving)
                {
                    escalation.Resolve(identity.Id, resolution!, _clock.UtcNow);
                }
                else
                {
                    escalation.Acknowledge(identity.Id, _clock.UtcNow);
                }

                await _escalations.SaveAsync(token).ConfigureAwait(false);

                return $"{verb} {escalationId}.";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one path every operator action takes: proposed as a human, gated, audited, executed.
    /// </summary>
    private async Task<OperatorOutcome> DispatchAsync(
        OperatorIdentity identity,
        Capability capability,
        ActionType actionType,
        ActionTarget target,
        OperatorActionParameters parameters,
        string idempotencyKey,
        Func<CancellationToken, Task<string>> effect,
        CancellationToken cancellationToken)
    {
        var proposal = ActionProposal.Create(
            _correlation.Current,
            capability,
            actionType,
            target,
            parameters,

            // An operator action changes what the platform is permitted to do; it commits no money
            // of its own. What acting on an opportunity would cost is the opportunity's economics,
            // and nobody is acting here.
            ActionEconomics.NoFinancialEffect(),

            // The identity that makes this whole surface worth having. It reaches the audit record's
            // actor, so the record of who decided is a person rather than a service account.
            ProposedBy.Human(identity.Id),
            idempotencyKey,
            _clock.UtcNow);

        var outcome = await _gateway
            .DispatchAsync(proposal, effect, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => OperatorOutcome.Done(outcome.Result ?? "Done."),
            ActionOutcomeStatus.Denied => new OperatorOutcome(
                OperatorOutcomeStatus.DeniedByPolicy, outcome.Reason),
            ActionOutcomeStatus.ApprovalRequired => new OperatorOutcome(
                OperatorOutcomeStatus.ApprovalRequired, outcome.Reason),
            ActionOutcomeStatus.DuplicateSuppressed => new OperatorOutcome(
                OperatorOutcomeStatus.DuplicateSuppressed, outcome.Reason),

            // Unreachable while ActionOutcomeStatus has the members it has. Present so that adding
            // one without updating this switch refuses rather than reporting success.
            _ => new OperatorOutcome(OperatorOutcomeStatus.DeniedByPolicy, outcome.Reason),
        };
    }

    /// <summary>The authenticated operator when they hold the privilege, and null otherwise.</summary>
    private OperatorIdentity? Authorise(OperatorPrivilege required)
    {
        var identity = _operators.Current;

        return identity is not null && identity.Has(required) ? identity : null;
    }

    /// <summary>
    /// Which of the two refusals applies, without leaking one as the other.
    /// </summary>
    /// <remarks>
    /// "Nobody is authenticated" and "you are authenticated and not permitted" are different facts
    /// and get different HTTP statuses. Collapsing them would make a privilege problem look like a
    /// login problem, and an operator would spend an incident re-entering a key that was fine.
    /// </remarks>
    private OperatorOutcome Refusal(OperatorPrivilege required) =>
        _operators.Current is null
            ? OperatorOutcome.NotAuthenticated()
            : OperatorOutcome.NotPermitted(required);
}
