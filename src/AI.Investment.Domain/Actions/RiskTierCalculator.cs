using AI.Investment.Domain.Enums;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// Computes the risk tier of a proposed action. Pure, total and deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Risk is computed, never asserted.</strong> No proposer - human, service or model -
/// supplies the risk tier of its own proposal. If it could, the classification would be exactly
/// as trustworthy as whatever produced the proposal, which defeats the purpose of having a
/// deterministic gate at all.
/// </para>
/// <para>
/// The rules read in order of severity and take the highest tier that applies. Reversibility
/// dominates amount: a small irreversible action outranks a large reversible one, because the
/// question that matters is not "how much" but "can this be taken back".
/// </para>
/// <para>
/// <strong>Deliberately absent: exposure bands.</strong> An obvious next rule would be
/// "exposure above X is High, above Y is Critical". It is not implemented, because thresholds
/// have to be currency-aware and this system has no FX conversion and no capital ledger yet.
/// Comparing a threshold in one currency against exposure in another would be precisely the
/// silent currency coercion that <c>Money</c> exists to prevent. Any non-zero exposure is
/// therefore treated as at least Medium, and banded thresholds arrive with the limit engine.
/// </para>
/// </remarks>
public static class RiskTierCalculator
{
    /// <summary>
    /// Calculates the risk tier for an action.
    /// </summary>
    /// <param name="capability">The class of thing the action does.</param>
    /// <param name="economics">What it costs, what it risks, and whether it can be undone.</param>
    /// <param name="isNovel">
    /// True when the action falls outside the pattern its capability has previously operated
    /// within. Novelty escalates by one tier: unfamiliar territory is not routine, and an
    /// autonomous system's confidence is weakest exactly where its experience is thinnest.
    /// Phase 1 always passes false; novelty detection needs an operating history to compare
    /// against, which arrives with continuous operation.
    /// </param>
    public static RiskTier Calculate(Capability capability, ActionEconomics economics, bool isNovel = false)
    {
        ArgumentNullException.ThrowIfNull(economics);

        var tier = BaseTierFor(capability);

        tier = Max(tier, TierForReversibility(economics.Reversibility));
        tier = Max(tier, TierForFinancialEffect(economics));

        if (isNovel)
        {
            tier = Escalate(tier);
        }

        return tier;
    }

    /// <summary>
    /// The floor imposed by the capability itself, regardless of amounts.
    /// </summary>
    private static RiskTier BaseTierFor(Capability capability) => capability switch
    {
        // Changing the safety system is the most dangerous class of action there is: it is the
        // action that can make every subsequent action possible. It is always Critical.
        Capability.PolicyAdministration => RiskTier.Critical,
        Capability.AutonomyAdministration => RiskTier.Critical,

        // Moving real money is Critical on its own terms.
        Capability.FinancialExecution => RiskTier.Critical,

        // Deciding an approval is the human gate; a system action here is high risk.
        Capability.ApprovalAdministration => RiskTier.High,

        // Spending money on providers and models, and committing to opportunities.
        Capability.DataIngestion => RiskTier.Low,
        Capability.Analysis => RiskTier.Low,
        Capability.OpportunityManagement => RiskTier.Medium,
        Capability.ReferenceDataManagement => RiskTier.Low,

        // Fail closed. An unrecognised capability - one added without updating this method -
        // is treated as maximally dangerous rather than defaulting to Low.
        _ => RiskTier.Critical,
    };

    private static RiskTier TierForReversibility(ReversibilityClass reversibility) => reversibility switch
    {
        ReversibilityClass.Reversible => RiskTier.Low,
        ReversibilityClass.ReversibleWithCost => RiskTier.Medium,
        ReversibilityClass.Irreversible => RiskTier.High,
        _ => RiskTier.Critical,
    };

    private static RiskTier TierForFinancialEffect(ActionEconomics economics)
    {
        if (economics.EstimatedExposure.IsPositive)
        {
            return RiskTier.Medium;
        }

        if (economics.EstimatedCost.IsPositive)
        {
            return RiskTier.Low;
        }

        return RiskTier.Low;
    }

    private static RiskTier Max(RiskTier left, RiskTier right) => left >= right ? left : right;

    private static RiskTier Escalate(RiskTier tier) =>
        tier >= RiskTier.Critical ? RiskTier.Critical : tier + 1;
}
