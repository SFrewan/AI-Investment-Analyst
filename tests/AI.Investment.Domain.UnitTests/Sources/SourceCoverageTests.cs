using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Sources;

/// <summary>
/// What a registry row may and may not learn after it was written.
/// </summary>
/// <remarks>
/// <para>
/// These rules exist because a connector outlives its registration. A build that adds an endpoint
/// gives the connector a category the stored row has never heard of, and until the row is told,
/// every request for that category is refused - correctly, and for a reason that reads like a
/// configuration error rather than a stale row.
/// </para>
/// <para>
/// The rules that matter are what a coverage change is allowed to disturb. Licensing, activation
/// and reliability are an operator's decisions; a source that was switched off must not come back
/// on because a build added an endpoint, and a source whose terms were narrowed must not have them
/// widened by the same accident.
/// </para>
/// </remarks>
public sealed class SourceCoverageTests
{
    [Fact]
    public void Coverage_can_be_widened_after_registration()
    {
        var source = SourceTestData.Register(categories: [DataCategory.RegulatoryFilings]);

        source.UpdateCoverage(
            [DataCategory.RegulatoryFilings, DataCategory.MarketWideDisclosure],
            SourceTestData.Now.AddDays(1));

        Assert.Contains(DataCategory.RegulatoryFilings, source.Categories);
        Assert.Contains(DataCategory.MarketWideDisclosure, source.Categories);
    }

    /// <summary>
    /// The refusal this whole path exists to clear: a category the row does not declare is not
    /// supplied, however capable the connector behind it has become.
    /// </summary>
    [Fact]
    public void A_source_does_not_supply_a_category_it_has_not_been_told_about()
    {
        var source = SourceTestData.Register(categories: [DataCategory.RegulatoryFilings]);

        Assert.False(source.Supplies(DataCategory.MarketWideDisclosure, Region.UnitedStates));

        source.UpdateCoverage(
            [DataCategory.RegulatoryFilings, DataCategory.MarketWideDisclosure],
            SourceTestData.Now.AddDays(1));

        Assert.True(source.Supplies(DataCategory.MarketWideDisclosure, Region.UnitedStates));
    }

    /// <summary>
    /// Widening coverage is not a back door to switching a source on, re-licensing it, or giving
    /// it a reliability it has not earned.
    /// </summary>
    [Fact]
    public void Widening_coverage_disturbs_nothing_else_about_the_row()
    {
        var source = SourceTestData.Active(categories: [DataCategory.RegulatoryFilings]);

        source.RecordReliability(ReliabilityGrade.Good, SourceTestData.Now);

        var licensing = source.Licensing;
        var verification = source.Verification;
        var registered = source.RegisteredAtUtc;

        source.UpdateCoverage(
            [DataCategory.RegulatoryFilings, DataCategory.MarketWideDisclosure],
            SourceTestData.Now.AddDays(1));

        Assert.True(source.IsActive);
        Assert.Equal(ReliabilityGrade.Good, source.Reliability);
        Assert.Equal(licensing, source.Licensing);
        Assert.Equal(verification, source.Verification);
        Assert.Equal(registered, source.RegisteredAtUtc);
    }

    /// <summary>
    /// A deactivated source stays deactivated. Coverage says what a source could answer, not
    /// whether the platform has decided to ask it.
    /// </summary>
    [Fact]
    public void Widening_coverage_does_not_reactivate_a_source_that_was_switched_off()
    {
        var source = SourceTestData.Active(categories: [DataCategory.RegulatoryFilings]);

        source.Deactivate(SourceTestData.Now);

        source.UpdateCoverage(
            [DataCategory.RegulatoryFilings, DataCategory.MarketWideDisclosure],
            SourceTestData.Now.AddDays(1));

        Assert.False(source.IsActive);
        Assert.Contains(DataCategory.MarketWideDisclosure, source.Categories);
    }

    /// <summary>
    /// <c>UpdateCoverage</c> replaces rather than merges, which is why every caller must pass the
    /// union it intends. Pinned here because a caller that passed only the new categories would
    /// silently revoke the old ones, and the revocation would look like a coverage gap rather
    /// than like a bug.
    /// </summary>
    [Fact]
    public void UpdateCoverage_replaces_the_set_rather_than_adding_to_it()
    {
        var source = SourceTestData.Register(categories:
            [DataCategory.RegulatoryFilings, DataCategory.CompanyProfile]);

        source.UpdateCoverage(
            [DataCategory.MarketWideDisclosure],
            SourceTestData.Now.AddDays(1));

        Assert.Single(source.Categories);
        Assert.DoesNotContain(DataCategory.RegulatoryFilings, source.Categories);
    }

    [Fact]
    public void Coverage_cannot_be_changed_before_the_source_was_registered()
    {
        var source = SourceTestData.Register();

        Assert.Throws<DomainRuleViolationException>(() =>
            source.UpdateCoverage(
                [DataCategory.MarketWideDisclosure],
                SourceTestData.Now.AddDays(-1)));
    }
}
