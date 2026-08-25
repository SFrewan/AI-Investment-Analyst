using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Sources;

/// <summary>
/// The ordering has to be total, stable and explainable - the same properties the policy engine
/// has, because "which source do we believe?" must be answerable identically every time.
/// </summary>
public sealed class SourceRankingTests
{
    /// <summary>
    /// Authority dominates. A fresher secondary source does not beat the filing it summarises.
    /// </summary>
    [Fact]
    public void Authority_outranks_everything_else()
    {
        var primary = SourceTestData.Register(id: "zzz-primary");
        var secondary = SourceTestData.Register(
            id: "aaa-secondary",
            authority: SourceAuthority.Secondary,
            verification: VerificationPolicy.RequiresCorroboration);
        secondary.RecordReliability(ReliabilityGrade.Excellent, SourceTestData.Now);

        var ordered = SourceRanking.MostAuthoritativeFirst([secondary, primary]);

        Assert.Same(primary, ordered[0]);
        Assert.Same(secondary, ordered[1]);
    }

    [Fact]
    public void A_self_sufficient_source_outranks_one_needing_corroboration()
    {
        var selfSufficient = SourceTestData.Register(id: "self");
        var needsHelp = SourceTestData.Register(
            id: "corroborate",
            verification: VerificationPolicy.RequiresCorroboration);

        var ordered = SourceRanking.MostAuthoritativeFirst([needsHelp, selfSufficient]);

        Assert.Same(selfSufficient, ordered[0]);
    }

    [Fact]
    public void Measured_reliability_breaks_a_tie_on_authority_and_policy()
    {
        var good = SourceTestData.Register(id: "aaa-good");
        good.RecordReliability(ReliabilityGrade.Good, SourceTestData.Now);
        var unrated = SourceTestData.Register(id: "bbb-unrated");

        var ordered = SourceRanking.MostAuthoritativeFirst([unrated, good]);

        Assert.Same(good, ordered[0]);
    }

    /// <summary>
    /// A source scoped to one market knows it better than a global one, but only once authority,
    /// self-sufficiency and reliability have failed to separate them.
    /// </summary>
    [Fact]
    public void A_regional_source_outranks_a_global_one_on_an_otherwise_exact_tie()
    {
        var regional = SourceTestData.Register(id: "zzz-regional", region: Region.UnitedStates);
        var global = SourceTestData.Register(id: "aaa-global", region: Region.Global);

        var ordered = SourceRanking.MostAuthoritativeFirst([global, regional]);

        Assert.Same(regional, ordered[0]);
    }

    /// <summary>
    /// The final tie-break exists so the order does not depend on enumeration order, which would
    /// make the same question answerable two ways.
    /// </summary>
    [Fact]
    public void Identical_sources_are_ordered_by_identifier()
    {
        var first = SourceTestData.Register(id: "aaa");
        var second = SourceTestData.Register(id: "bbb");

        var forwards = SourceRanking.MostAuthoritativeFirst([first, second]);
        var backwards = SourceRanking.MostAuthoritativeFirst([second, first]);

        Assert.Same(first, forwards[0]);
        Assert.Same(first, backwards[0]);
    }

    [Fact]
    public void MostAuthoritative_returns_null_for_an_empty_set() =>
        Assert.Null(SourceRanking.MostAuthoritative([]));

    [Fact]
    public void MostAuthoritative_agrees_with_the_head_of_the_ordered_list()
    {
        var primary = SourceTestData.Register(id: "primary");
        var secondary = SourceTestData.Register(
            id: "secondary",
            authority: SourceAuthority.Secondary,
            verification: VerificationPolicy.RequiresCorroboration);

        var sources = new[] { secondary, primary };

        Assert.Same(
            SourceRanking.MostAuthoritativeFirst(sources)[0],
            SourceRanking.MostAuthoritative(sources));
    }
}
