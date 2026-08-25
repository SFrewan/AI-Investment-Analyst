using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Sources;

/// <summary>
/// The data plane's gate. Like the policy engine it is pure, total and fail-closed, so it is
/// tested the same way: every refusal path by name, not just the happy one.
/// </summary>
public sealed class SourceAdmissionTests
{
    private static readonly DataCategory Filings = DataCategory.RegulatoryFilings;

    [Fact]
    public void An_active_licensed_covering_source_is_admitted()
    {
        var source = SourceTestData.Active(categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, Filings, Region.UnitedStates);

        Assert.True(result.IsAdmitted);
        Assert.Null(result.RuleId);
    }

    /// <summary>Registration records that a source was assessed, not that it may be used.</summary>
    [Fact]
    public void A_registered_but_inactive_source_is_refused()
    {
        var source = SourceTestData.Register(categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, Filings, Region.UnitedStates);

        Assert.False(result.IsAdmitted);
        Assert.Equal(SourceAdmission.SourceActiveRule, result.RuleId);
    }

    [Fact]
    public void An_unknown_category_is_refused()
    {
        var source = SourceTestData.Active(categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, DataCategory.Unknown, Region.UnitedStates);

        Assert.Equal(SourceAdmission.CategoryRecognisedRule, result.RuleId);
    }

    /// <summary>
    /// Fail closed on an enum value this build does not recognise, exactly as the policy engine
    /// does.
    /// </summary>
    [Fact]
    public void An_unrecognised_category_is_refused()
    {
        var source = SourceTestData.Active(categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, (DataCategory)9999, Region.UnitedStates);

        Assert.Equal(SourceAdmission.CategoryRecognisedRule, result.RuleId);
    }

    [Fact]
    public void A_category_the_source_does_not_declare_is_refused()
    {
        var source = SourceTestData.Active(categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, DataCategory.MarketPrices, Region.UnitedStates);

        Assert.Equal(SourceAdmission.SuppliesCategoryRule, result.RuleId);
    }

    [Fact]
    public void A_region_the_source_does_not_cover_is_refused()
    {
        var source = SourceTestData.Active(region: Region.UnitedStates, categories: [Filings]);

        var result = SourceAdmission.Evaluate(source, Filings, Region.Create("GB"));

        Assert.Equal(SourceAdmission.SuppliesCategoryRule, result.RuleId);
    }

    /// <summary>
    /// Checked before retrieval, because by the time a response has been fetched and written
    /// down, an impermissible ingestion has already happened.
    /// </summary>
    [Fact]
    public void A_source_that_may_not_be_stored_is_refused()
    {
        var source = SourceTestData.Active(
            categories: [Filings],
            licensing: SourceTestData.ProcessingOnly());

        var result = SourceAdmission.Evaluate(source, Filings, Region.UnitedStates);

        Assert.Equal(SourceAdmission.StoragePermittedRule, result.RuleId);
    }

    [Fact]
    public void A_source_that_may_not_be_processed_automatically_is_refused()
    {
        var source = SourceTestData.Active(
            categories: [Filings],
            licensing: SourceTestData.StorageOnly());

        var result = SourceAdmission.Evaluate(source, Filings, Region.UnitedStates);

        Assert.Equal(SourceAdmission.ProcessingPermittedRule, result.RuleId);
    }

    /// <summary>
    /// A source whose terms nobody established permits nothing, so it fails the licensing rules
    /// rather than slipping through them. It cannot be activated either - this asserts the
    /// gate would refuse it even if it somehow were.
    /// </summary>
    [Fact]
    public void Unknown_licensing_permits_nothing()
    {
        var source = SourceTestData.Register(categories: [Filings], licensing: LicensingTerms.Unknown);

        Assert.False(source.IsActive);
        Assert.False(LicensingTerms.Unknown.StorageAllowed);
        Assert.False(LicensingTerms.Unknown.AutomatedProcessingAllowed);
        Assert.False(LicensingTerms.Unknown.RedistributionAllowed);
    }

    [Fact]
    public void Admissible_filters_out_refused_sources_and_orders_the_rest()
    {
        var inactive = SourceTestData.Register(id: "inactive", categories: [Filings]);
        var secondary = SourceTestData.Active(
            id: "secondary",
            authority: SourceAuthority.Secondary,
            categories: [Filings],
            verification: VerificationPolicy.RequiresCorroboration);
        var primary = SourceTestData.Active(id: "primary", categories: [Filings]);
        var wrongCategory = SourceTestData.Active(id: "wrong", categories: [DataCategory.News]);

        var admitted = SourceAdmission.Admissible(
            [inactive, secondary, primary, wrongCategory],
            Filings,
            Region.UnitedStates);

        Assert.Equal(2, admitted.Count);
        Assert.Same(primary, admitted[0]);
        Assert.Same(secondary, admitted[1]);
    }

    [Fact]
    public void Admissible_returns_an_empty_list_when_nothing_qualifies()
    {
        var inactive = SourceTestData.Register(categories: [Filings]);

        Assert.Empty(SourceAdmission.Admissible([inactive], Filings, Region.UnitedStates));
    }
}
