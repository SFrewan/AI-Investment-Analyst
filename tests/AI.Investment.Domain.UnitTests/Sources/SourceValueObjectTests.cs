using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Sources;

public sealed class SourceIdTests
{
    /// <summary>
    /// Normalisation is what makes the identifier a reliable key. "SEC-EDGAR" and "sec-edgar"
    /// naming two different sources would defeat the entire registry.
    /// </summary>
    [Theory]
    [InlineData("SEC-EDGAR", "sec-edgar")]
    [InlineData("  fred  ", "fred")]
    [InlineData("Internal.Analysis-Engine", "internal.analysis-engine")]
    public void An_identifier_is_lower_cased_and_trimmed(string input, string expected) =>
        Assert.Equal(expected, SourceId.Create(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sec edgar")]
    [InlineData("sec_edgar")]
    [InlineData("sec:edgar")]
    [InlineData("sec/edgar")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    public void An_identifier_that_could_not_be_a_registry_key_is_rejected(string input) =>
        Assert.Throws<DomainValidationException>(() => SourceId.Create(input));

    [Fact]
    public void An_identifier_may_not_exceed_the_maximum_length() =>
        Assert.Throws<DomainValidationException>(() =>
            SourceId.Create(new string('a', SourceId.MaxLength + 1)));

    [Fact]
    public void Identifiers_compare_by_value() =>
        Assert.Equal(SourceId.Create("sec-edgar"), SourceId.Create("SEC-EDGAR"));
}

public sealed class RegionTests
{
    [Fact]
    public void A_country_code_is_upper_cased() => Assert.Equal("US", Region.Create("us").Code);

    [Fact]
    public void Global_is_recognised_by_name()
    {
        Assert.True(Region.Create("global").IsGlobal);
        Assert.Same(Region.Global, Region.Create("GLOBAL"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("U")]
    [InlineData("USA")]
    [InlineData("U1")]
    public void An_invalid_region_code_is_rejected(string code) =>
        Assert.Throws<DomainValidationException>(() => Region.Create(code));

    [Fact]
    public void Global_covers_every_region()
    {
        Assert.True(Region.Global.Covers(Region.UnitedStates));
        Assert.True(Region.Global.Covers(Region.Create("JP")));
    }

    [Fact]
    public void A_country_covers_only_itself()
    {
        Assert.True(Region.UnitedStates.Covers(Region.UnitedStates));
        Assert.False(Region.UnitedStates.Covers(Region.Create("GB")));
        Assert.False(Region.UnitedStates.Covers(Region.Global));
    }
}

public sealed class VerificationPolicyTests
{
    [Fact]
    public void A_self_sufficient_policy_confirms_on_one_source()
    {
        Assert.Equal(ConfirmationState.Unverified, VerificationPolicy.Authoritative.Classify(0));
        Assert.Equal(ConfirmationState.Confirmed, VerificationPolicy.Authoritative.Classify(1));
    }

    /// <summary>
    /// One agreeing source under a policy that needs two is not "partially confirmed" - there is
    /// nothing to be partial about until a second source agrees.
    /// </summary>
    [Fact]
    public void A_corroborating_policy_needs_the_stated_number_of_sources()
    {
        var policy = VerificationPolicy.RequiresCorroboration;

        Assert.Equal(ConfirmationState.Unverified, policy.Classify(0));
        Assert.Equal(ConfirmationState.Unverified, policy.Classify(1));
        Assert.Equal(ConfirmationState.Confirmed, policy.Classify(2));
        Assert.Equal(ConfirmationState.Confirmed, policy.Classify(3));
    }

    [Fact]
    public void A_cautious_policy_reports_partial_confirmation_on_the_way()
    {
        var policy = VerificationPolicy.Cautious;

        Assert.Equal(ConfirmationState.Unverified, policy.Classify(1));
        Assert.Equal(ConfirmationState.PartiallyConfirmed, policy.Classify(2));
        Assert.Equal(ConfirmationState.Confirmed, policy.Classify(3));
    }

    [Fact]
    public void A_source_that_confirms_alone_requires_exactly_itself() =>
        Assert.Throws<DomainValidationException>(() => VerificationPolicy.Create(true, 2));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void An_out_of_range_corroboration_count_is_rejected(int count) =>
        Assert.Throws<DomainValidationException>(() => VerificationPolicy.Create(false, count));
}

public sealed class UpdateCadenceTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A filing arrives when it arrives. Calling it stale on a timer would mean permanently
    /// alarming about a source behaving exactly as expected.
    /// </summary>
    [Fact]
    public void An_event_driven_source_is_never_overdue() =>
        Assert.False(UpdateCadence.EventDriven.IsOverdue(Now.AddYears(-5), Now, TimeSpan.Zero));

    [Fact]
    public void An_on_demand_source_is_never_overdue() =>
        Assert.False(UpdateCadence.OnDemand.IsOverdue(Now.AddYears(-5), Now, TimeSpan.Zero));

    [Fact]
    public void A_daily_source_is_overdue_after_its_interval_plus_grace()
    {
        var daily = UpdateCadence.Daily();

        Assert.False(daily.IsOverdue(Now.AddDays(-1), Now, TimeSpan.Zero));
        Assert.True(daily.IsOverdue(Now.AddDays(-2), Now, TimeSpan.Zero));
        Assert.False(daily.IsOverdue(Now.AddDays(-2), Now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void A_cadence_without_an_interval_cannot_be_given_one()
    {
        Assert.Throws<DomainValidationException>(() =>
            UpdateCadence.Every(CadenceKind.EventDriven, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            UpdateCadence.Every(CadenceKind.OnDemand, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void A_non_positive_interval_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            UpdateCadence.Every(CadenceKind.Daily, TimeSpan.Zero));
}

public sealed class LicensingTermsTests
{
    /// <summary>
    /// The default direction matters: unknown terms must not read as permissive, because the
    /// consequence of getting it wrong is a compliance problem rather than a bug.
    /// </summary>
    [Fact]
    public void Unknown_terms_permit_nothing()
    {
        Assert.False(LicensingTerms.Unknown.StorageAllowed);
        Assert.False(LicensingTerms.Unknown.RedistributionAllowed);
        Assert.False(LicensingTerms.Unknown.AutomatedProcessingAllowed);
        Assert.True(LicensingTerms.Unknown.AttributionRequired);
    }

    [Fact]
    public void Open_data_permits_storage_redistribution_and_processing()
    {
        var terms = LicensingTerms.OpenData();

        Assert.True(terms.StorageAllowed);
        Assert.True(terms.RedistributionAllowed);
        Assert.True(terms.AutomatedProcessingAllowed);
    }

    [Fact]
    public void Notes_may_not_exceed_the_maximum_length() =>
        Assert.Throws<DomainValidationException>(() =>
            LicensingTerms.OpenData(new string('x', LicensingTerms.MaxNotesLength + 1)));
}
