using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>
/// The complete path from an approved opportunity to a filled order and a balanced set of books.
/// </summary>
/// <remarks>
/// <para>
/// Five gates, in this order, and the order is the design:
/// </para>
/// <list type="number">
/// <item><strong>Kill switch.</strong> Read here as well as by the policy engine. The execution path
/// re-validates rather than trusting its caller, because a component that can be talked into acting
/// by whoever calls it is not a control.</item>
/// <item><strong>Limits.</strong> Evaluated against the ledger's current exposure, before anything
/// is consumed or dispatched.</item>
/// <item><strong>Policy.</strong> Through the action gateway, which is the only thing that may
/// invoke an effect.</item>
/// <item><strong>Approval.</strong> Consumed inside the effect - so a denied action leaves the token
/// unused - and consumed atomically by the store, so a concurrent caller cannot use it twice.</item>
/// <item><strong>Venue and ledger.</strong> The order is placed and the books are posted in the same
/// step, so a fill cannot exist without entries describing it.</item>
/// </list>
/// <para>
/// <strong>A consumed approval is spent even when the venue refuses.</strong> The action was
/// attempted, and the conservative reading of "we tried and it did not work" is that a person
/// decides again rather than that the system retries on an old permission.
/// </para>
/// </remarks>
public sealed class OpportunityExecutor
{
    private readonly IActionGateway _gateway;
    private readonly IApprovalTokenStore _tokens;
    private readonly IExecutionVenue _venue;
    private readonly ILedgerStore _ledger;
    private readonly ILimitProvider _limits;
    private readonly IExposureProvider _exposure;
    private readonly IKillSwitch _killSwitch;
    private readonly IOpportunityRepository _opportunities;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWriteAuthorization _writeAuthorization;
    private readonly IClock _clock;

    public OpportunityExecutor(
        IActionGateway gateway,
        IApprovalTokenStore tokens,
        IExecutionVenue venue,
        ILedgerStore ledger,
        ILimitProvider limits,
        IExposureProvider exposure,
        IKillSwitch killSwitch,
        IOpportunityRepository opportunities,
        IUnitOfWork unitOfWork,
        IWriteAuthorization writeAuthorization,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _opportunities = opportunities ?? throw new ArgumentNullException(nameof(opportunities));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _writeAuthorization = writeAuthorization ?? throw new ArgumentNullException(nameof(writeAuthorization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ExecutionOutcome> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = request.Order.Price.Currency;

        var killSwitch = await _killSwitch
            .ReadAsync(request.Proposal.Capability, cancellationToken)
            .ConfigureAwait(false);

        if (killSwitch != KillSwitchState.Disengaged)
        {
            return ExecutionOutcome.Refused(
                ExecutionStatus.RefusedByKillSwitch,
                killSwitch == KillSwitchState.Unknown
                    ? "The kill switch state could not be determined, which is treated exactly like " +
                      "engaged. A switch of unknown state is a switch that is on."
                    : "The kill switch is engaged. Nothing executes.");
        }

        var limits = await _limits.GetAsync(cancellationToken).ConfigureAwait(false);
        var exposure = await _exposure.GetAsync(currency, cancellationToken).ConfigureAwait(false);

        var verdict = LimitEngine.Evaluate(request.Proposal, exposure, limits, _clock.UtcNow);

        if (!verdict.IsAllowed)
        {
            return ExecutionOutcome.RefusedByLimits(verdict);
        }

        var refusal = ApprovalRefusal.None;
        VenueResult? venueResult = null;

        var outcome = await _gateway.DispatchAsync(
            request.Proposal,
            async token =>
            {
                refusal = await _tokens.ConsumeAsync(
                    request.ApprovalTokenId,
                    request.Opportunity.OpportunityId,
                    request.Proposal,
                    _clock.UtcNow,
                    token).ConfigureAwait(false);

                if (refusal != ApprovalRefusal.None)
                {
                    return false;
                }

                venueResult = await _venue.PlaceAsync(request.Order, token).ConfigureAwait(false);

                if (venueResult.Filled)
                {
                    await _ledger
                        .AppendAsync(Postings(request, venueResult.RequireFill()), token)
                        .ConfigureAwait(false);
                }

                return venueResult.Filled;
            },
            cancellationToken).ConfigureAwait(false);

        return await ResolveAsync(request, outcome, refusal, venueResult, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the gateway's outcome, the approval result and the venue's answer into one verdict.
    /// </summary>
    /// <remarks>
    /// The effect returns a boolean rather than throwing on a refusal, so that the gateway records a
    /// completed execution either way. An exception would abandon the audit record of the attempt,
    /// and an attempt that was made and refused is exactly the thing worth recording.
    /// </remarks>
    private async Task<ExecutionOutcome> ResolveAsync(
        ExecutionRequest request,
        ActionOutcome<bool> outcome,
        ApprovalRefusal refusal,
        VenueResult? venueResult,
        CancellationToken cancellationToken)
    {
        switch (outcome.Status)
        {
            case ActionOutcomeStatus.Denied:
                return ExecutionOutcome.Refused(ExecutionStatus.DeniedByPolicy, outcome.Reason);

            case ActionOutcomeStatus.ApprovalRequired:
                return ExecutionOutcome.Refused(ExecutionStatus.ApprovalRequired, outcome.Reason);

            case ActionOutcomeStatus.DuplicateSuppressed:
                return ExecutionOutcome.Refused(ExecutionStatus.DuplicateSuppressed, outcome.Reason);

            default:
                break;
        }

        if (refusal != ApprovalRefusal.None)
        {
            return ExecutionOutcome.RefusedByApproval(
                refusal,
                $"The approval could not authorise this action: {refusal}.");
        }

        if (venueResult is null || !venueResult.Filled)
        {
            return ExecutionOutcome.Refused(
                ExecutionStatus.VenueRejected,
                venueResult?.Refusal ?? "The venue was never reached.");
        }

        var nowUtc = _clock.UtcNow;
        var opportunity = request.Opportunity;

        opportunity.BeginExecution(nowUtc);
        opportunity.Activate(outcome.Execution?.ExecutionId ?? Guid.NewGuid(), nowUtc);

        // The aggregate's transition is part of the effect the policy engine authorised, and the
        // gateway's window closed when the effect returned - so the same decision is used to open
        // one here. It is the decision reached for THIS proposal, not a fresh permission: the
        // persistence guard still refuses a write that no decision authorises, which is the
        // property Phase 1 exists to hold.
        using (_writeAuthorization.Authorize(outcome.Decision))
        {
            await _opportunities.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return ExecutionOutcome.Filled(venueResult.RequireFill());
    }

    /// <summary>
    /// The double-entry postings for one fill.
    /// </summary>
    /// <remarks>
    /// A purchase moves cash into positions and pays a fee. A disposal moves it back, and records a
    /// realised result only when the caller knew what the position cost - inventing a basis would put
    /// a fabricated profit into the one ledger that must not contain one.
    /// </remarks>
    private static List<LedgerEntry> Postings(ExecutionRequest request, VenueFill fill)
    {
        var entries = new List<LedgerEntry>();
        var opportunityId = request.Opportunity.OpportunityId;
        var notional = fill.Notional;

        if (request.Order.Side == OrderSide.Buy)
        {
            entries.Add(LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.Cash,
                notional,
                fill.FilledAtUtc,
                $"Bought {request.Order.Instrument}",
                opportunityId));
        }
        else
        {
            entries.Add(LedgerEntry.Post(
                LedgerAccount.Cash,
                LedgerAccount.Positions,
                notional,
                fill.FilledAtUtc,
                $"Sold {request.Order.Instrument}",
                opportunityId));

            if (request.CostBasis is { } basis)
            {
                AddRealisedResult(entries, basis, notional, fill.FilledAtUtc, request);
            }
        }

        if (fill.Fees.IsPositive)
        {
            entries.Add(LedgerEntry.Post(
                LedgerAccount.Fees,
                LedgerAccount.Cash,
                fill.Fees,
                fill.FilledAtUtc,
                $"Fees on {request.Order.Instrument}",
                opportunityId));
        }

        return entries;
    }

    private static void AddRealisedResult(
        List<LedgerEntry> entries,
        Money basis,
        Money proceeds,
        DateTime filledAtUtc,
        ExecutionRequest request)
    {
        if (proceeds.IsGreaterThan(basis))
        {
            entries.Add(LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.RealisedGains,
                proceeds.Subtract(basis),
                filledAtUtc,
                $"Realised gain on {request.Order.Instrument}",
                request.Opportunity.OpportunityId));

            return;
        }

        if (basis.IsGreaterThan(proceeds))
        {
            entries.Add(LedgerEntry.Post(
                LedgerAccount.RealisedLosses,
                LedgerAccount.Positions,
                basis.Subtract(proceeds),
                filledAtUtc,
                $"Realised loss on {request.Order.Instrument}",
                request.Opportunity.OpportunityId));
        }
    }
}
