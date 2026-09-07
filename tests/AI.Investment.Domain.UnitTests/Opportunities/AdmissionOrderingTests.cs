using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// That a measured strategy always outranks an unmeasured one, whatever it claims.
/// </summary>
/// <remarks>
/// <para>
/// Sorting a mixed pool by score is not a neutral choice between strategies. It is a choice in
/// favour of whichever strategy is most optimistic, because a comparison of two decimals cannot tell
/// a number that was checked from a number that was asserted. A rule written this morning that
/// states 0.99 would take the action from a measured rule stating 0.55 every single time, and nobody
/// would have decided that - it would simply be what <c>&gt;</c> does.
/// </para>
/// <para>
/// So these are property tests rather than expected-order tests. The property is the guarantee; a
/// hand-written expected ordering would assert the same thing less clearly and would need rewriting
/// whenever the fixture moved.
/// </para>
/// </remarks>
public sealed class AdmissionOrderingTests
{
    private static readonly OpportunityType Measured = OpportunityType.Create("equity-price-recovery");
    private static readonly OpportunityType Unmeasured = OpportunityType.Create("equity-brand-new");

    /// <summary>
    /// <strong>The property.</strong> No unadmitted candidate ever precedes an admitted one.
    /// </summary>
    [Fact]
    public void An_unmeasured_strategy_never_outranks_a_measured_one()
    {
        var ordered = AdmissionOrdering.Order(
        [
            Candidate(Unmeasured, 0.99m, admitted: false),
            Candidate(Measured, 0.10m, admitted: true),
            Candidate(Unmeasured, 0.95m, admitted: false),
            Candidate(Measured, 0.55m, admitted: true),
        ]);

        var lastAdmitted = ordered
            .Select((candidate, index) => (candidate, index))
            .Where(entry => entry.candidate.StrategyAdmitted)
            .Max(entry => entry.index);

        var firstUnadmitted = ordered
            .Select((candidate, index) => (candidate, index))
            .Where(entry => !entry.candidate.StrategyAdmitted)
            .Min(entry => entry.index);

        Assert.True(lastAdmitted < firstUnadmitted);

        // And the highest score in the pool belongs to a strategy that does not lead it.
        Assert.Equal(0.99m, ordered.Max(candidate => candidate.Score));
        Assert.True(ordered[0].StrategyAdmitted);
        Assert.Equal(0.55m, ordered[0].Score);
    }

    /// <summary>Inside a tier the score does its ordinary job.</summary>
    [Fact]
    public void Within_a_tier_the_higher_score_leads()
    {
        var ordered = AdmissionOrdering.Order(
        [
            Candidate(Measured, 0.20m, admitted: true),
            Candidate(Measured, 0.80m, admitted: true),
            Candidate(Unmeasured, 0.30m, admitted: false),
            Candidate(Unmeasured, 0.70m, admitted: false),
        ]);

        Assert.Equal(0.80m, ordered[0].Score);
        Assert.Equal(0.20m, ordered[1].Score);
        Assert.Equal(0.70m, ordered[2].Score);
        Assert.Equal(0.30m, ordered[3].Score);
    }

    /// <summary>Nothing is dropped: an unadmitted candidate is still recorded and still visible.</summary>
    /// <remarks>
    /// A strategy earning its record has to keep producing candidates, or it never accumulates the
    /// resolved predictions that would admit it. Ranking excludes it from acting, not from existing.
    /// </remarks>
    [Fact]
    public void Ordering_keeps_every_candidate_and_acting_keeps_only_the_admitted_ones()
    {
        var pool = new[]
        {
            Candidate(Unmeasured, 0.99m, admitted: false),
            Candidate(Measured, 0.55m, admitted: true),
            Candidate(Unmeasured, 0.88m, admitted: false),
        };

        Assert.Equal(3, AdmissionOrdering.Order(pool).Count);
        Assert.Single(AdmissionOrdering.Actionable(pool));
        Assert.All(AdmissionOrdering.Actionable(pool), c => Assert.True(c.StrategyAdmitted));
    }

    /// <summary>
    /// A pool with nothing admitted yields nothing to act on, rather than the best of a bad set.
    /// </summary>
    /// <remarks>
    /// This is the state the platform is actually in, and it is the assertion that matters most. A
    /// gate that silently fell back to the highest-scoring unadmitted candidate whenever it had
    /// nothing admitted would be a gate in name only, and the fallback would look like sensible
    /// defensive coding right up until it acted on an unmeasured rule.
    /// </remarks>
    [Fact]
    public void A_pool_with_nothing_admitted_offers_nothing_to_act_on()
    {
        var pool = new[]
        {
            Candidate(Unmeasured, 0.99m, admitted: false),
            Candidate(Unmeasured, 0.97m, admitted: false),
        };

        Assert.Empty(AdmissionOrdering.Actionable(pool));
        Assert.Null(AdmissionOrdering.Best(pool));
        Assert.Equal(2, AdmissionOrdering.Order(pool).Count);
    }

    [Fact]
    public void The_best_candidate_is_the_leading_admitted_one()
    {
        var best = AdmissionOrdering.Best(
        [
            Candidate(Unmeasured, 1.00m, admitted: false),
            Candidate(Measured, 0.42m, admitted: true),
            Candidate(Measured, 0.61m, admitted: true),
        ]);

        Assert.NotNull(best);
        Assert.Equal(0.61m, best!.Score);
    }

    /// <summary>Ties keep the order the caller supplied rather than one the sort invented.</summary>
    [Fact]
    public void Candidates_on_the_same_score_keep_the_order_they_arrived_in()
    {
        var first = Candidate(Measured, 0.50m, admitted: true);
        var second = Candidate(Measured, 0.50m, admitted: true);

        var ordered = AdmissionOrdering.Order([first, second]);

        Assert.Equal(first.OpportunityId, ordered[0].OpportunityId);
        Assert.Equal(second.OpportunityId, ordered[1].OpportunityId);
    }

    /// <summary>A score off the common scale would win or lose on its units rather than its merits.</summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void A_score_outside_the_common_scale_is_refused(double score) =>
        Assert.Throws<DomainValidationException>(() =>
            AdmissionOrdering.EnsureComparable([Candidate(Measured, (decimal)score, admitted: true)]));

    [Fact]
    public void A_pool_on_the_common_scale_is_accepted() =>
        AdmissionOrdering.EnsureComparable(
        [
            Candidate(Measured, 0m, admitted: true),
            Candidate(Measured, 1m, admitted: true),
        ]);

    private static RankableOpportunity Candidate(OpportunityType type, decimal score, bool admitted) =>
        new(OpportunityId.New(), type, score, admitted);
}
