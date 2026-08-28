using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>
/// Builds the <see cref="ActionProposal"/> for placing one order at the simulated venue.
/// </summary>
/// <remarks>
/// <para>
/// Assembling the proposal is deterministic service work, not agent work: it is a structured record
/// built from an opportunity and an order, and every field a model could influence arrives as an
/// evidence claim rather than as a number typed into the proposal.
/// </para>
/// <para>
/// <strong>The capability is <see cref="Capability.SimulatedExecution"/>, never
/// <see cref="Capability.FinancialExecution"/>.</strong> The two are separate so that the whole
/// path can be exercised while the one capability that moves real money stays refused
/// unconditionally by a structural rule the policy engine evaluates before any configuration.
/// Nothing in this file can produce a proposal under the other capability.
/// </para>
/// <para>
/// <strong>The risk tier is not set here.</strong> It is computed by the domain from the capability,
/// the reversibility and the economics. A proposer that could state the risk of its own proposal
/// would make the classification exactly as trustworthy as whatever produced it.
/// </para>
/// </remarks>
public static class SimulatedExecutionProposal
{
    /// <summary>The action type recorded on every simulated order.</summary>
    public static ActionType Type { get; } = ActionType.Create("execution.simulated-order");

    /// <summary>
    /// Reversibility of a simulated position change.
    /// </summary>
    /// <remarks>
    /// <c>ReversibleWithCost</c> rather than <c>Reversible</c>: a position can be closed, and doing
    /// so pays a spread and a second commission. Recording it as freely reversible would understate
    /// the tier of every trade the platform ever proposes.
    /// </remarks>
    public const ReversibilityClass Reversibility = ReversibilityClass.ReversibleWithCost;

    public static ActionProposal For(
        Opportunity opportunity,
        VenueOrder order,
        ProposedBy proposedBy,
        CorrelationId correlationId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(proposedBy);

        var economics = ActionEconomics.Create(
            Money.Zero(order.Price.Currency),
            order.Notional.Abs(),
            Reversibility);

        return ActionProposal.Create(
            correlationId,
            Capability.SimulatedExecution,
            Type,
            ActionTarget.Create("Instrument", order.Instrument),
            new OrderParameters(
                order.Instrument,
                order.Side,
                order.Quantity,
                order.Price.Amount,
                order.Price.Currency.Code),
            economics,
            proposedBy,
            order.IdempotencyKey,
            nowUtc,
            evidence: opportunity.Evidence,
            confidence: opportunity.Confidence);
    }
}
