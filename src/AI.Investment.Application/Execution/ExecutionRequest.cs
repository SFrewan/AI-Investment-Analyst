using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>Everything needed to act on one approved opportunity, once.</summary>
/// <remarks>
/// <see cref="CostBasis"/> is supplied only when disposing, and only when the caller knows what the
/// position originally cost. Without it a sale posts cash and positions and records no realised
/// result, which is honest: this phase does not track lots, and inventing a basis would put a
/// fabricated profit into the one ledger that must not contain one. Position tracking arrives with
/// the validation phase, which is the first thing that actually needs it.
/// </remarks>
public sealed record ExecutionRequest
{
    private ExecutionRequest(
        Opportunity opportunity,
        ActionProposal proposal,
        Guid approvalTokenId,
        VenueOrder order,
        Money? costBasis)
    {
        Opportunity = opportunity;
        Proposal = proposal;
        ApprovalTokenId = approvalTokenId;
        Order = order;
        CostBasis = costBasis;
    }

    public Opportunity Opportunity { get; }

    public ActionProposal Proposal { get; }

    public Guid ApprovalTokenId { get; }

    public VenueOrder Order { get; }

    /// <summary>What the position being sold originally cost, when that is known.</summary>
    public Money? CostBasis { get; }

    public static ExecutionRequest Create(
        Opportunity opportunity,
        ActionProposal proposal,
        Guid approvalTokenId,
        VenueOrder order,
        Money? costBasis = null)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(order);

        if (approvalTokenId == Guid.Empty)
        {
            throw new DomainRuleViolationException(
                "ExecutionRequest.RequiresApproval",
                "An execution must name the approval that permitted it.");
        }

        if (order.OpportunityId != opportunity.OpportunityId)
        {
            throw new DomainRuleViolationException(
                "ExecutionRequest.OrderBelongsToOpportunity",
                "The order names a different opportunity from the one being executed.");
        }

        if (order.ApprovalTokenId != approvalTokenId)
        {
            throw new DomainRuleViolationException(
                "ExecutionRequest.OrderBelongsToApproval",
                "The order names a different approval from the one being consumed.");
        }

        if (costBasis is not null && costBasis.Currency != order.Price.Currency)
        {
            throw new DomainValidationException(
                nameof(costBasis),
                $"The cost basis is in {costBasis.Currency} but the order is in {order.Price.Currency}.");
        }

        return new ExecutionRequest(opportunity, proposal, approvalTokenId, order, costBasis);
    }
}
