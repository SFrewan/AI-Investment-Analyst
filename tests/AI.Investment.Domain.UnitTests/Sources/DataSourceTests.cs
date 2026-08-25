using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Sources;

/// <summary>
/// The registry's structural rules. These are the rules that stop a mistaken or malicious entry
/// from granting a source standing it has not earned, so they are tested as rules rather than as
/// incidental behaviour.
/// </summary>
public sealed class DataSourceTests
{
    [Fact]
    public void A_source_registers_inactive()
    {
        var source = SourceTestData.Register();

        Assert.False(source.IsActive);
    }

    /// <summary>
    /// Reliability is earned by measurement. If it could be declared at registration, every
    /// source would be registered excellent.
    /// </summary>
    [Fact]
    public void A_source_registers_unrated()
    {
        var source = SourceTestData.Register();

        Assert.Equal(ReliabilityGrade.Unrated, source.Reliability);
    }

    /// <summary>
    /// Without this rule, a registration that skipped the authority question could quietly mint
    /// confirmed facts.
    /// </summary>
    [Fact]
    public void An_unverified_source_may_not_confirm_alone()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            SourceTestData.Register(
                authority: SourceAuthority.Unverified,
                type: SourceType.NewsOrganisation,
                verification: VerificationPolicy.Authoritative));

        Assert.Equal("DataSource.UnverifiedCannotConfirmAlone", exception.Rule);
    }

    [Fact]
    public void An_unverified_source_that_requires_corroboration_registers()
    {
        var source = SourceTestData.Register(
            authority: SourceAuthority.Unverified,
            type: SourceType.NewsOrganisation,
            verification: VerificationPolicy.RequiresCorroboration);

        Assert.Equal(SourceAuthority.Unverified, source.Authority);
    }

    /// <summary>
    /// An aggregator republishes someone else's record by definition, so it cannot be the
    /// originating one whatever the registration claims.
    /// </summary>
    [Fact]
    public void An_aggregator_may_not_be_primary()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            SourceTestData.Register(
                type: SourceType.CommunityOrAggregator,
                authority: SourceAuthority.Primary));

        Assert.Equal("DataSource.AggregatorIsNotPrimary", exception.Rule);
    }

    [Fact]
    public void An_aggregator_may_be_secondary()
    {
        var source = SourceTestData.Register(
            type: SourceType.CommunityOrAggregator,
            authority: SourceAuthority.Secondary,
            verification: VerificationPolicy.RequiresCorroboration);

        Assert.Equal(SourceType.CommunityOrAggregator, source.Type);
    }

    /// <summary>
    /// Catching this at activation matters: the alternative is discovering it after an ingestion
    /// run has already retained data the terms forbid.
    /// </summary>
    [Fact]
    public void A_source_permitting_neither_storage_nor_processing_cannot_be_activated()
    {
        var source = SourceTestData.Register(licensing: LicensingTerms.Unknown);

        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            source.Activate(SourceTestData.Now));

        Assert.Equal("DataSource.ActivationRequiresUsableLicence", exception.Rule);
        Assert.False(source.IsActive);
    }

    [Fact]
    public void Activation_succeeds_when_storage_alone_is_permitted()
    {
        var source = SourceTestData.Register(licensing: SourceTestData.StorageOnly());

        source.Activate(SourceTestData.Now);

        Assert.True(source.IsActive);
    }

    /// <summary>
    /// Terms narrow after a legal review. A feed already switched on must not outlive its
    /// permission.
    /// </summary>
    [Fact]
    public void Narrowing_the_licence_to_nothing_deactivates_an_active_source()
    {
        var source = SourceTestData.Active();
        Assert.True(source.IsActive);

        source.UpdateLicensing(LicensingTerms.Unknown, SourceTestData.Now.AddDays(1));

        Assert.False(source.IsActive);
    }

    [Fact]
    public void Widening_the_licence_does_not_activate_an_inactive_source()
    {
        var source = SourceTestData.Register(licensing: LicensingTerms.Unknown);

        source.UpdateLicensing(LicensingTerms.OpenData(), SourceTestData.Now.AddDays(1));

        Assert.False(source.IsActive);
    }

    [Fact]
    public void A_source_must_declare_at_least_one_category() =>
        Assert.Throws<DomainValidationException>(() => SourceTestData.Register(categories: []));

    /// <summary>
    /// "Unknown" is the absence of a coverage claim, not a coverage claim, and a source declaring
    /// it could be routed to for anything.
    /// </summary>
    [Fact]
    public void Unknown_is_not_a_coverage_claim() =>
        Assert.Throws<DomainValidationException>(() =>
            SourceTestData.Register(categories: [DataCategory.Unknown]));

    [Fact]
    public void An_unrecognised_category_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            SourceTestData.Register(categories: [(DataCategory)9999]));

    [Fact]
    public void An_unrecognised_authority_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            SourceTestData.Register(authority: (SourceAuthority)9999));

    [Fact]
    public void Supplies_requires_both_the_category_and_the_region()
    {
        var source = SourceTestData.Register(
            region: Region.UnitedStates,
            categories: [DataCategory.MarketPrices]);

        Assert.True(source.Supplies(DataCategory.MarketPrices, Region.UnitedStates));
        Assert.False(source.Supplies(DataCategory.News, Region.UnitedStates));
        Assert.False(source.Supplies(DataCategory.MarketPrices, Region.Create("GB")));
    }

    [Fact]
    public void A_global_source_covers_a_specific_region()
    {
        var source = SourceTestData.Register(
            region: Region.Global,
            categories: [DataCategory.ForeignExchange]);

        Assert.True(source.Supplies(DataCategory.ForeignExchange, Region.Create("JP")));
    }

    [Fact]
    public void Coverage_can_be_updated()
    {
        var source = SourceTestData.Register(categories: [DataCategory.RegulatoryFilings]);

        source.UpdateCoverage([DataCategory.News, DataCategory.CompanyProfile], SourceTestData.Now);

        Assert.Equal(2, source.Categories.Count);
        Assert.Contains(DataCategory.News, source.Categories);
        Assert.DoesNotContain(DataCategory.RegulatoryFilings, source.Categories);
    }

    [Fact]
    public void Reliability_is_recorded_separately_from_registration()
    {
        var source = SourceTestData.Register();

        source.RecordReliability(ReliabilityGrade.Good, SourceTestData.Now.AddDays(30));

        Assert.Equal(ReliabilityGrade.Good, source.Reliability);
    }

    [Fact]
    public void A_source_cannot_be_modified_before_it_was_registered()
    {
        var source = SourceTestData.Register();

        Assert.Throws<DomainRuleViolationException>(() =>
            source.Activate(SourceTestData.Now.AddDays(-1)));
    }

    /// <summary>
    /// Self-sufficiency is authority plus policy. A primary source that the platform has decided
    /// to corroborate anyway is not authoritative for this purpose.
    /// </summary>
    [Fact]
    public void IsAuthoritative_requires_primary_authority_and_a_self_sufficient_policy()
    {
        Assert.True(SourceTestData.Register().IsAuthoritative);

        Assert.False(SourceTestData.Register(
            verification: VerificationPolicy.RequiresCorroboration).IsAuthoritative);

        Assert.False(SourceTestData.Register(
            authority: SourceAuthority.Secondary,
            verification: VerificationPolicy.RequiresCorroboration).IsAuthoritative);
    }
}
