using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>One candidate in the ranked pool, and whether its strategy has earned a place there.</summary>
/// <param name="OpportunityId">The candidate.</param>
/// <param name="Type">The strategy that produced it.</param>
/// <param name="Score">Its dimensionless rank score.</param>
/// <param name="StrategyAdmitted">Whether that strategy cleared the admission bar.</param>
public sealed record RankableOpportunity(
    OpportunityId OpportunityId,
    OpportunityType Type,
    decimal Score,
    bool StrategyAdmitted);

/// <summary>
/// Orders candidates so that a measured strategy always outranks an unmeasured one.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the gate expressed as an ordering. Sorting by score alone is not a
/// neutral choice between strategies - it is a choice in favour of whichever strategy is most
/// optimistic, because the comparison cannot tell a checked number from an asserted one. A brand new
/// rule stating 0.95 would take the action from a measured rule stating 0.55 every time, and nobody
/// would have decided that.
/// </para>
/// <para>
/// So admission is the primary key and score is the secondary key. Within each tier the score does
/// its ordinary job; across the tiers it does not compete at all. An unadmitted candidate can be
/// shown, logged, recorded and measured - <see cref="Order"/> keeps it in the list - but
/// <see cref="Actionable"/> is what may reach a proposal, and it contains only candidates whose
/// strategy has a record.
/// </para>
/// </remarks>
public static class AdmissionOrdering
{
    /// <summary>
    /// Every candidate, admitted strategies first, each tier by descending score.
    /// </summary>
    /// <remarks>
    /// Stable within a tier, so two candidates on the same score keep the order the caller supplied
    /// rather than an order that depends on the sort's internals.
    /// </remarks>
    public static IReadOnlyList<RankableOpportunity> Order(
        IEnumerable<RankableOpportunity> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(entry => entry.candidate.StrategyAdmitted)
            .ThenByDescending(entry => entry.candidate.Score)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.candidate)
            .ToList();
    }

    /// <summary>
    /// The candidates that may compete for an action: admitted strategies only, best first.
    /// </summary>
    public static IReadOnlyList<RankableOpportunity> Actionable(
        IEnumerable<RankableOpportunity> candidates) =>
        Order(candidates).Where(candidate => candidate.StrategyAdmitted).ToList();

    /// <summary>
    /// The candidate that would be acted on, or null when no admitted strategy produced one.
    /// </summary>
    /// <remarks>
    /// Null is the correct and expected answer whenever every strategy is still earning its record,
    /// and it must stay distinguishable from "the best candidate scored zero". A platform that
    /// silently fell back to the best unadmitted candidate when it had nothing admitted would have
    /// the gate in name only.
    /// </remarks>
    public static RankableOpportunity? Best(IEnumerable<RankableOpportunity> candidates)
    {
        var actionable = Actionable(candidates);

        return actionable.Count == 0 ? null : actionable[0];
    }

    /// <summary>
    /// Refuses a pool whose scores are not on one scale, which is what makes ranking them lawful.
    /// </summary>
    /// <remarks>
    /// <see cref="OpportunityScore"/> already refuses anything that is not a dimensionless ratio, so
    /// this guards the remaining hole: a ratio outside [0,1] is not comparable with one inside it,
    /// and would win or lose on its units rather than on its merits.
    /// </remarks>
    public static void EnsureComparable(IEnumerable<RankableOpportunity> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            if (candidate.Score is < 0m or > 1m)
            {
                throw new DomainValidationException(
                    nameof(candidates),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{candidate.Type}' scored {candidate.Score}, outside [0,1].") +
                    " Ranking it against a normalised score would decide the ordering on scale " +
                    "rather than on merit.");
            }
        }
    }
}
